using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace VieNLink.Mobile;

/// <summary>Что подбираем: страницу YouTube или поток видео-CDN.</summary>
internal enum AutotuneScope
{
    Page,
    Video,
}

/// <summary>Как подбираем: первая рабочая (быстро) или лучшая из всех (дольше).</summary>
internal enum AutotuneMode
{
    Quick,
    Best,
}

/// <summary>Чем проверять домен: страница, картинка-превью или API приложения.</summary>
internal enum ProbeKind
{
    Html,
    Image,
    Api,
}

/// <summary>Результат подбора стратегии для выбранного набора доменов.</summary>
internal sealed record AutotuneResult(
    AutotuneScope Scope,
    StreamFragmentStrategy? Strategy,
    int Score,
    int Total,
    bool Strong = false,
    int SpeedKbps = 0,
    bool AudioOnly = false)
{
    public bool HasAny => Strategy is not null;
}

/// <summary>
/// Шаг подбора для интерфейса и журнала: что ищем, какая стратегия по счёту из скольких,
/// и готовая строка для показа («Страница YouTube · 3/25: «OOB» — проверяю…»).
/// </summary>
internal sealed record AutotuneStep(AutotuneScope Scope, int Index, int Total, string Text);

/// <summary>
/// Быстрая проверка текущей стратегии через уже работающий обход. <see cref="Strong"/> —
/// пришли настоящие байты и страницы, и превью; один HTML ничего не доказывает.
/// </summary>
internal readonly record struct PageProbe(int Score, bool Html, bool Image, bool Api, int SpeedKbps = 0)
{
    public bool Strong => Html && Image;

    /// <summary>Короткое описание для итога.</summary>
    public string Describe =>
        Strong ? "страница и превью" :
        Html ? "только страница" :
        Score > 0 ? "частично" : "ничего";

    /// <summary>Подробно по доменам — для строки прогресса.</summary>
    public string Details =>
        $"страница {(Html ? "✓" : "✗")}, превью {(Image ? "✓" : "✗")}, API {(Api ? "✓" : "✗")}";
}

/// <summary>
/// Подбор стратегии десинка на самом устройстве по доменам приложения YouTube.
/// Приложение исключено из VPN, поэтому запросы идут напрямую в сеть провайдера, но через
/// отдельный SOCKS5-форвардер — DPI проверяет их так же, как реальный трафик приложения.
/// Форвардер поднимается свой, на свободном порту: подбор можно делать, не ломая текущий обход.
/// </summary>
internal static class StrategyAutotune
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object ActiveGate = new();
    private static CancellationTokenSource? _active;

    // Домены страницы: сам сайт, превью и API приложения. Реальные URL, а не /generate_204:
    // последний возвращает пустой ответ (0 байт) и даёт ложные провалы.
    // Проверяется не только код ответа, но и настоящие байты: HTML-страница без превью —
    // это «картинка загрузилась, а видео нет», такую стратегию рабочей не считаем.
    private static readonly (string Url, ProbeKind Kind)[] PageUrls =
    {
        ("https://www.youtube.com/", ProbeKind.Html),
        ("https://i.ytimg.com/vi/n683WmHzOrs/hqdefault.jpg", ProbeKind.Image),
        ("https://youtubei.googleapis.com/youtubei/v1/browse", ProbeKind.Api),
    };

    // Ссылки на медиапотоки берём со страницы просмотра: без подписи они протухают
    // и отдают HTML ошибки. Видео и звук — отдельные потоки (разные itag), DPI может
    // резать их по-разному, поэтому проверяем оба.
    private const string VideoWatchUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw";

    private const string VideoProbeUrl =
        "https://redirector.googlevideo.com/videoplayback?id=jNQXAC9IVRw&itag=18";

    /// <summary>Тип медиапотока для пробы.</summary>
    private enum MediaKind
    {
        Video,
        Audio,
    }

    // Свежие подписанные ссылки: живут 15 минут, потом берём заново.
    private static (string? Video, string? Audio, DateTime At) _mediaUrls;

    // Диагностика: пишем в журнал, почему проба медиа не удалась (не больше нескольких строк).
    private static int _mediaLogCount;

    /// <summary>Страница просмотра не отдала ссылки: перебор видео бессмысленен.</summary>
    private static bool _mediaUrlsMissing;

    // Поток должен реально идти: 64 КБ — это больше, чем «заморозка после ~25 пакетов»
    // у ТСПУ, поэтому 4 КБ (прежний порог) давали ложное «видео работает».
    private const int VideoMinBytes = 64 * 1024;

    /// <summary>Сколько байт потока читаем для замера скорости (3 секунды или этот объём).</summary>
    private const int VideoSampleBytes = 512 * 1024;

    // Звук — отдельный поток: он меньше, поэтому порог и окно чтения ниже.
    private const int AudioMinBytes = 24 * 1024;

    private const int AudioSampleBytes = 128 * 1024;

    /// <summary>Идёт ли подбор прямо сейчас (его можно прервать из интерфейса).</summary>
    public static bool IsRunning
    {
        get
        {
            lock (ActiveGate)
            {
                return _active is not null;
            }
        }
    }

    /// <summary>
    /// Прервать текущий подбор: пользователь включил обход или нажал «Остановить подбор».
    /// Сам подбор вернёт управление через <see cref="OperationCanceledException"/>.
    /// </summary>
    public static void CancelActive()
    {
        CancellationTokenSource? active;
        lock (ActiveGate)
        {
            active = _active;
        }

        try
        {
            active?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Подбор уже завершился и освободил источник.
        }
    }

    /// <summary>
    /// Проверка через уже работающий обход: страница, превью, API. «Рабочая» — когда пришли
    /// байты и страницы, и превью. Один HTML не доказывает, что видео пойдёт: так и появлялось
    /// «стратегия рабочая, а на экране только картинка».
    /// </summary>
    public static Task<PageProbe> CheckPageAsync(int port, CancellationToken cancellationToken) =>
        ProbePageAsync(port, cancellationToken);

    public static async Task<AutotuneResult> RunAsync(
        AutotuneScope scope,
        IProgress<AutotuneStep> progress,
        CancellationToken cancellationToken,
        StreamFragmentMode? preferred = null,
        AutotuneMode mode = AutotuneMode.Quick)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (ActiveGate)
        {
            _active = linked;
        }

        try
        {
            await Gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                return await RunCoreAsync(scope, progress, linked.Token, preferred, mode)
                    .ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }
        finally
        {
            lock (ActiveGate)
            {
                if (ReferenceEquals(_active, linked))
                {
                    _active = null;
                }
            }
        }
    }

    /// <summary>
    /// Порядок перебора: сначала сохранённый/зашитый пресет этой сети (если он есть), затем
    /// OOB и «Разрез TLS-записей» — они чаще работают на Android, — затем остальные в порядке
    /// ядра. Иначе подбор тратил минуты на fake-стратегии и не проверял известную рабочую.
    /// </summary>
    private static List<StreamFragmentStrategy> OrderedStrategies(StreamFragmentMode? preferred) =>
        TlsFragmentForwarder.Strategies
            .Select((strategy, index) => (Strategy: strategy, Index: index))
            .OrderByDescending(item => preferred is not null && item.Strategy.Mode == preferred)
            .ThenByDescending(item => item.Strategy.Mode == StreamFragmentMode.Oob)
            .ThenByDescending(item => item.Strategy.Mode == StreamFragmentMode.RecordSplit)
            .ThenBy(item => item.Index)
            .Select(item => item.Strategy)
            .ToList();

    private static async Task<AutotuneResult> RunCoreAsync(
        AutotuneScope scope,
        IProgress<AutotuneStep> progress,
        CancellationToken cancellationToken,
        StreamFragmentMode? preferred,
        AutotuneMode mode)
    {
        var phase = scope == AutotuneScope.Video ? "Видео (googlevideo)" : "Страница YouTube";
        var totalProbes = scope == AutotuneScope.Video ? 1 : PageUrls.Length;

        StreamFragmentStrategy? best = null;
        var bestScore = -1;
        var bestStrong = false;
        var bestSpeed = 0;
        var bestVideo = false;
        var bestAudioOnly = false;
        var bestDetails = "ничего не прошло";
        var passed = 0;
        var strategies = OrderedStrategies(preferred);
        var total = strategies.Count;
        var index = 0;

        foreach (var strategy in strategies)
        {
            index++;
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new AutotuneStep(scope, index, total,
                $"{phase} · {index}/{total}: «{strategy.Name}» — проверяю…"));

            var forwarder = new TlsFragmentForwarder();
            int port;
            try
            {
                await forwarder.StartAsync(strategy.Mode, 0, fragmentAllHosts: true)
                    .ConfigureAwait(false);
                port = forwarder.Port;
            }
            catch (Exception exception)
            {
                progress.Report(new AutotuneStep(scope, index, total,
                    $"{phase} · «{strategy.Name}»: не запустился ({exception.Message})"));
                await forwarder.StopAsync().ConfigureAwait(false);
                continue;
            }

            try
            {
                if (scope == AutotuneScope.Video)
                {
                    var video = await ProbeMediaAsync(port, MediaKind.Video, cancellationToken)
                        .ConfigureAwait(false);
                    // Если видео не идёт — проверяем звук: он отдельным потоком и может проходить.
                    var audio = video.Ok || _mediaUrlsMissing
                        ? default
                        : await ProbeMediaAsync(port, MediaKind.Audio, cancellationToken)
                            .ConfigureAwait(false);
                    var videoOk = video.Ok;
                    var audioOnly = !videoOk && audio.Ok;
                    var score = videoOk ? 1 : 0;
                    var speed = videoOk ? video.SpeedKbps : audio.SpeedKbps;
                    var details = videoOk
                        ? $"видео {video.Bytes / 1024} КБ"
                        : audioOnly
                            ? $"только звук {audio.Bytes / 1024} КБ"
                            : "0 байт";
                    progress.Report(new AutotuneStep(scope, index, total,
                        $"{phase} · «{strategy.Name}»: {score}/{totalProbes} — {details}" +
                        (speed > 0 ? $" · {speed} КБ/с" : string.Empty)));

                    if (videoOk || audioOnly)
                    {
                        passed++;
                    }

                    // Видео важнее звука: если видео нет ни у кого, берём самый быстрый звук.
                    var better = videoOk
                        ? !bestVideo || speed > bestSpeed
                        : audioOnly && !bestVideo && (!bestAudioOnly || speed > bestSpeed);
                    if (better)
                    {
                        bestScore = videoOk ? 1 : 0;
                        best = strategy;
                        bestSpeed = speed;
                        bestVideo = videoOk;
                        bestAudioOnly = audioOnly;
                        bestDetails = details;
                    }

                    // Быстрый режим: годится и видео, и (если видео нет) работающий звук.
                    if ((videoOk || audioOnly) && mode == AutotuneMode.Quick)
                    {
                        break;
                    }

                    // Ссылок нет — перебирать 26 стратегий бессмысленно, все упрутся в то же.
                    if (_mediaUrlsMissing && !videoOk)
                    {
                        progress.Report(new AutotuneStep(scope, index, total,
                            $"{phase}: страница не отдала ссылки на поток — перебор остановлен"));
                        break;
                    }
                }
                else
                {
                    var probe = await ProbePageAsync(port, cancellationToken).ConfigureAwait(false);
                    progress.Report(new AutotuneStep(scope, index, total,
                        $"{phase} · «{strategy.Name}»: {probe.Score}/{totalProbes} — {probe.Details}" +
                        (probe.Score > 0 ? $" · {probe.SpeedKbps} КБ/с" : string.Empty)));
                    if (probe.Score > 0)
                    {
                        passed++;
                    }

                    // Лучшая — больше доменов, затем «страница + превью», затем скорость.
                    var better = probe.Score > bestScore ||
                                 (probe.Score == bestScore &&
                                  ((probe.Strong && !bestStrong) ||
                                   (probe.Strong == bestStrong && probe.SpeedKbps > bestSpeed)));
                    if (better)
                    {
                        bestScore = probe.Score;
                        best = strategy;
                        bestStrong = probe.Strong;
                        bestSpeed = probe.SpeedKbps;
                        bestDetails = probe.Details;
                    }

                    // Быстрый режим — первая стратегия со страницей и превью;
                    // полный — проверяем все и выбираем быстрее.
                    if (probe.Strong && mode == AutotuneMode.Quick)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                progress.Report(new AutotuneStep(scope, index, total,
                    $"{phase} · «{strategy.Name}»: проба не удалась ({exception.Message})"));
            }
            finally
            {
                await forwarder.StopAsync().ConfigureAwait(false);
            }
        }

        var result = bestScore > 0 || bestAudioOnly ? best : null;
        var prefix = mode == AutotuneMode.Quick ? "первая рабочая" : "лучшая";
        var final = result is null
            ? $"{phase}: ни одна из {total} стратегий не прошла проверку"
            : $"{phase}: {prefix} «{result.Name}» — {Math.Max(bestScore, 0)}/{totalProbes} ({bestDetails})" +
              (bestSpeed > 0 ? $" · {bestSpeed} КБ/с" : string.Empty) +
              (mode == AutotuneMode.Best ? $", прошли {passed} из {total}" : string.Empty);
        progress.Report(new AutotuneStep(scope, total, total, final));
        return new AutotuneResult(scope, result, Math.Max(bestScore, 0), totalProbes, bestStrong,
            bestSpeed, bestAudioOnly);
    }

    private static async Task<PageProbe> ProbePageAsync(int port, CancellationToken cancellationToken)
    {
        var html = await ProbeAsync(port, PageUrls[0].Url, ProbeKind.Html, cancellationToken)
            .ConfigureAwait(false);
        if (!html.Ok)
        {
            // Без страницы остальное не имеет смысла: не тратим секунды на превью и API.
            return new PageProbe(0, false, false, false);
        }

        var image = await ProbeAsync(port, PageUrls[1].Url, ProbeKind.Image, cancellationToken)
            .ConfigureAwait(false);
        var api = await ProbeAsync(port, PageUrls[2].Url, ProbeKind.Api, cancellationToken)
            .ConfigureAwait(false);

        var bytes = html.Bytes + image.Bytes;
        var elapsed = Math.Max(html.ElapsedMs + image.ElapsedMs, 1);
        var speed = (int)Math.Round(bytes * 1000.0 / elapsed / 1024.0);
        return new PageProbe(1 + (image.Ok ? 1 : 0) + (api.Ok ? 1 : 0), true, image.Ok, api.Ok, speed);
    }

    /// <summary>
    /// Качает реальные байты медиапотока через rr*-узел. Успех — только если узел отдал поток
    /// (200, тип медиа, порог байтов за 3 с), а не 403/HTML/0 байт (SNI-блок, протухшая
    /// подпись или заморозка после ~25 пакетов). Для видео и звука пороги разные.
    /// </summary>
    private static async Task<(bool Ok, int Bytes, int SpeedKbps)> ProbeMediaAsync(
        int port, MediaKind kind, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                PooledConnectionLifetime = TimeSpan.Zero,
                AllowAutoRedirect = true, // редиректим на rr*-узел и качаем оттуда
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12),
            };

            var (videoUrl, audioUrl) = await ResolveMediaUrlsAsync(client, cancellationToken)
                .ConfigureAwait(false);
            var url = kind == MediaKind.Video ? videoUrl ?? VideoProbeUrl : audioUrl;
            if (url is null)
            {
                LogMedia($"«{kind}»: ссылки нет");
                return (false, 0, 0);
            }

            using var response = await client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                LogMedia($"«{kind}»: статус {(int)response.StatusCode}, хост " +
                         (response.RequestMessage?.RequestUri?.Host ?? "—"));
                return (false, 0, 0);
            }

            // Редирект должен привести именно на медиасервер googlevideo, а тип — быть медиа:
            // так HTML-страница ошибки (200 + 5 КБ) не считается «работающим видео».
            var host = response.RequestMessage?.RequestUri?.Host ?? string.Empty;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var expected = kind == MediaKind.Video ? "video/" : "audio/";
            if (!host.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                (!mediaType.StartsWith(expected, StringComparison.OrdinalIgnoreCase) &&
                 !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)))
            {
                LogMedia($"«{kind}»: тип «{mediaType}», хост {host} — не медиапоток");
                return (false, 0, 0);
            }

            // Звук маленький: ему достаточно меньшего порога и окна чтения.
            var minimum = kind == MediaKind.Video ? VideoMinBytes : AudioMinBytes;
            var sample = kind == MediaKind.Video ? VideoSampleBytes : AudioSampleBytes;
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var buffer = new byte[16 * 1024];
            var total = 0;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (total < sample)
                {
                    var read = await stream.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 3 секунды на замер потока — этого хватает, чтобы отличить быструю
                // стратегию от «еле дотянула до порога».
            }

            var speed = (int)Math.Round(total * 1000.0 / Math.Max(watch.ElapsedMilliseconds, 1) / 1024.0);
            return (total >= minimum, total, speed);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or IOException or InvalidOperationException)
        {
            return (false, 0, 0);
        }
    }

    /// <summary>
    /// Берёт свежие подписанные ссылки на видео и звук со страницы просмотра. Ссылка без
    /// подписи протухает и отдаёт HTML ошибки — из-за этого проба видео всегда показывала
    /// 0 байт. Кэш 15 минут: за один перебор страница качается один раз.
    /// </summary>
    private static async Task<(string? Video, string? Audio)> ResolveMediaUrlsAsync(
        HttpClient client, CancellationToken cancellationToken)
    {
        if (_mediaUrls.At != default && DateTime.UtcNow - _mediaUrls.At < TimeSpan.FromMinutes(15))
        {
            _mediaUrlsMissing = _mediaUrls.Video is null && _mediaUrls.Audio is null;
            return (_mediaUrls.Video, _mediaUrls.Audio);
        }

        try
        {
            using var response = await client.GetAsync(
                VideoWatchUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                _mediaUrlsMissing = true;
                AppLog.Write("tun-bypass",
                    $"страница видео: статус {(int)response.StatusCode} — подписанных ссылок нет");
                return (null, null);
            }

            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(6));
            var buffer = new byte[64 * 1024];
            var text = new StringBuilder();
            var total = 0;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (total < 768 * 1024)
                {
                    var read = await stream.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    total += read;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Не успели прочитать страницу целиком — ищем ссылки в прочитанном.
            }

            // В объекте формата mimeType идёт ПЕРЕД url, вложенные объекты (initRange)
            // мешают разбору по фигурным скобкам — поэтому пары ищем по близости индексов.
            var textValue = text.ToString();
            var types = Regex.Matches(textValue, "\"mimeType\"\\s*:\\s*\"(video|audio)");
            var urls = Regex.Matches(textValue,
                "\"url\"\\s*:\\s*\"(https:\\\\/\\\\/rr[^\"]*videoplayback[^\"]*)\"");
            string? video = null;
            string? audio = null;
            foreach (Match urlMatch in urls)
            {
                var type = "video";
                Match? nearest = null;
                foreach (Match typeMatch in types)
                {
                    if (typeMatch.Index < urlMatch.Index && urlMatch.Index - typeMatch.Index < 4000)
                    {
                        nearest = typeMatch;
                    }
                }

                if (nearest is not null)
                {
                    type = nearest.Groups[1].Value;
                }

                var url = urlMatch.Groups[1].Value.Replace("\\/", "/").Replace("\\u0026", "&");
                if (type == "video" && video is null)
                {
                    video = url;
                }
                else if (type == "audio" && audio is null)
                {
                    audio = url;
                }

                if (video is not null && audio is not null)
                {
                    break;
                }
            }

            _mediaUrls = (video, audio, DateTime.UtcNow);
            _mediaUrlsMissing = video is null && audio is null;
            AppLog.Write("tun-bypass",
                $"страница видео: ссылки — видео {(video is not null ? "есть" : "нет")}, " +
                $"звук {(audio is not null ? "есть" : "нет")}");
            return (video, audio);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or IOException or InvalidOperationException)
        {
            _mediaUrlsMissing = true;
            AppLog.Write("tun-bypass", "страница видео не открылась: " + exception.Message);
            return (null, null);
        }
    }

    /// <summary>
    /// Проверяет, проходит ли QUIC (UDP/443) через релей: отправляет настоящий Initial из
    /// шаблона на www.google.com и ждёт любой ответ. QUIC не зависит от TCP-стратегии,
    /// поэтому проверяется один раз на весь подбор, а не для каждой стратегии.
    /// </summary>
    public static async Task<bool> ProbeQuicAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            var stream = tcp.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken).ConfigureAwait(false);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, cancellationToken)
                .ConfigureAwait(false);
            var reply = new byte[10];
            await stream.ReadExactlyAsync(reply, cancellationToken).ConfigureAwait(false);
            if (reply[0] != 5 || reply[1] != 0)
            {
                return false;
            }

            var relay = new IPEndPoint(new IPAddress(reply.AsSpan(4, 4)), (reply[8] << 8) | reply[9]);
            var addresses = await Dns.GetHostAddressesAsync("www.google.com", AddressFamily.InterNetwork,
                cancellationToken).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                return false;
            }

            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var template = QuicFake.Template;
            if (template.Length < QuicFake.MinimumLength)
            {
                return false;
            }

            var packet = new byte[10 + template.Length];
            packet[3] = 1;
            addresses[0].GetAddressBytes().CopyTo(packet, 4);
            packet[8] = 443 >> 8;
            packet[9] = 443 & 0xFF;
            template.Span.CopyTo(packet.AsSpan(10));
            await udp.SendToAsync(packet, SocketFlags.None, relay, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var buffer = new byte[65535];
            try
            {
                var received = await udp.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token)
                    .ConfigureAwait(false);
                // Любой ответ сервера (Version Negotiation/Retry) — значит UDP/443 проходит.
                return received.ReceivedBytes > 10;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                              or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void LogMedia(string line)
    {
        if (Interlocked.Increment(ref _mediaLogCount) <= 8)
        {
            AppLog.Write("tun-bypass", "видео-проба: " + line);
        }
    }

    /// <summary>
    /// Запрос через форвардер с проверкой того, что важно для каждого типа домена:
    /// страница — HTML, превью — картинка, API — любой ответ кроме 5xx. Возвращает
    /// байты и время: по ним считается скорость для сравнения стратегий.
    /// </summary>
    private static async Task<(bool Ok, int Bytes, long ElapsedMs)> ProbeAsync(
        int port,
        string url,
        ProbeKind kind,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.Zero,
                // Не следовать редиректам у API: 3xx от самого домена — уже успех.
                AllowAutoRedirect = kind != ProbeKind.Api,
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8),
            };
            using var response = await client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (kind == ProbeKind.Api)
            {
                return (code < 500, 0, watch.ElapsedMilliseconds);
            }

            if (code != 200)
            {
                return (false, 0, watch.ElapsedMilliseconds);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (kind == ProbeKind.Html &&
                !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return (false, 0, watch.ElapsedMilliseconds);
            }

            if (kind == ProbeKind.Image &&
                !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return (false, 0, watch.ElapsedMilliseconds);
            }

            var minimum = kind == ProbeKind.Html ? 2 * 1024 : 4 * 1024;
            var cap = kind == ProbeKind.Html ? 64 * 1024 : 32 * 1024;
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var buffer = new byte[8 * 1024];
            var total = 0;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (total < cap)
                {
                    var read = await stream.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Не успели прочитать за 3 секунды — судим по тому, что уже пришло.
            }

            return (total >= minimum, total, watch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or IOException or InvalidOperationException)
        {
            return (false, 0, watch.ElapsedMilliseconds);
        }
    }
}
