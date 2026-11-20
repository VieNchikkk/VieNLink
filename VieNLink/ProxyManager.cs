using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace VieNLink;

/// <summary>
/// Управляет жизненным циклом ядра: проверка узлов через Clash API, подключение,
/// системный прокси и контроль, что sing-box не упал.
/// </summary>
internal sealed class ProxyManager : IAsyncDisposable
{
    /// <summary>Проверочные адреса: узел считается рабочим, если открывает хотя бы один.</summary>
    public const string YoutubeTestUrl = "https://www.youtube.com/";

    public const string TelegramTestUrl = "https://t.me/";

    /// <summary>
    /// Telegram живёт на нескольких доменах: мессенджер, веб-версия и сайт. Узел получает
    /// отметку «Telegram», если открылся хотя бы один — иначе рабочий узел браковался бы
    /// из-за блокировки одного адреса.
    /// </summary>
    public static IReadOnlyList<string> TelegramTestUrls { get; } = new[]
    {
        "https://t.me/",
        "https://web.telegram.org/",
        "https://telegram.org/",
    };

    private readonly SingBoxRunner _runner = new();
    private SystemProxyBackup _proxyBackup = new();
    private ClashApiClient? _api;
    private string _apiSecret = string.Empty;
    private string _loadedSignature = string.Empty;
    private List<ProxyNode> _loadedNodes = new();
    private readonly HashSet<string> _loadedTags = new(StringComparer.Ordinal);

    /// <summary>Узлы, которые sing-box не принял (несовместимый формат): их нужно удалить из списка.</summary>
    private readonly List<ProxyNode> _incompatible = new();
    private int _mixedPort;
    private int _apiPort;
    private System.Threading.Timer? _healthTimer;
    private bool _proxyApplied;
    private int _healthBusy;
    private DateTime _connectedAtUtc;
    private DateTime _nextHealthCheckUtc;
    private int _healthFailures;
    private CancellationTokenSource? _statsCts;
    private long _totalUp;
    private long _totalDown;

    /// <summary>Адреса проверки для текущего подключения: YouTube обычно, несколько доменов Telegram в его режиме.</summary>
    private IReadOnlyList<string>? _probeUrls;

    public ProxyManager(AppSettings settings)
    {
        Settings = settings;
        _runner.LogLine += line => AppLog.Write("sing-box", line);
    }

    public AppSettings Settings { get; }

    public bool IsConnected { get; private set; }

    /// <summary>Kill switch удерживает системный прокси на закрытом порту: трафик заблокирован до «Отключиться».</summary>
    public bool IsKillSwitchHeld { get; private set; }

    public bool IsRunning => _runner.IsRunning;

    /// <summary>Узел уже загружен в работающее ядро — переключение на него не требует перезапуска.</summary>
    public bool IsNodeLoaded(ProxyNode node) => _loadedTags.Contains(node.Tag);

    public ProxyNode? CurrentNode { get; private set; }

    public int ActiveMixedPort => _mixedPort > 0 ? _mixedPort : Settings.MixedPort;

    /// <summary>Узлы, которые сейчас загружены в работающее ядро.</summary>
    public IReadOnlyList<ProxyNode> LoadedNodes => _loadedNodes;

    /// <summary>Ядро упало или прокси отключён извне — UI должен обновить состояние.</summary>
    public event Action<string>? StateChanged;

    /// <summary>Узел подключён, но перестал открывать проверочный адрес — пора переключаться.</summary>
    public event Action<string>? NodeUnhealthy;

    /// <summary>Отсчёт статистики трафика: скорость и накопленный объём за сессию.</summary>
    public event Action<TrafficSample>? TrafficUpdated;

    public async Task EnsureCoreAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await SingBoxCore.EnsureAsync(progress, cancellationToken);
    }

    /// <summary>
    /// Поднимает ядро с указанными узлами без проверки — нужно для замера скорости,
    /// когда пользователь ещё не подключён.
    /// </summary>
    public Task PrepareTestCoreAsync(
        IReadOnlyList<ProxyNode> nodes,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        EnsureTestCoreAsync(nodes, progress, cancellationToken);

    /// <summary>
    /// Проверяет узлы по двум адресам (YouTube и Telegram) и заполняет отметки в узле.
    /// Не открыл ни один — узел остаётся в списке, пока не наберёт
    /// <see cref="NodeLimits.RemoveAfterFailures"/> провалов подряд; затем удаляется.
    /// </summary>
    public async Task<TestSummary> TestNodesAsync(
        IReadOnlyList<ProxyNode> nodes,
        IProgress<string>? progress,
        Action<ProxyNode>? onNodeTested,
        CancellationToken cancellationToken,
        int concurrency = 0,
        int timeoutMs = 3500,
        string? youtubeUrl = null,
        string? telegramUrl = null)
    {
        if (nodes.Count == 0)
        {
            return new TestSummary(0, 0, 0, 0);
        }

        await EnsureTestCoreAsync(nodes, progress, cancellationToken);

        // Узлы, которые sing-box не принял (например, чужой отпечаток uTLS), проверять
        // нечем: без тега в ядре проба падает мгновенно. Убираем их из списка навсегда,
        // иначе они занимают место в каждой партии и она никогда не набирает 500.
        if (_incompatible.Count > 0)
        {
            var skipped = _incompatible.ToList();
            _incompatible.Clear();
            foreach (var broken in skipped)
            {
                broken.FailCount = NodeLimits.RemoveAfterFailures;
                broken.Status = NodeStatus.Dead;
                broken.CheckedAtUtc = DateTime.UtcNow;
                onNodeTested?.Invoke(broken);
            }

            progress?.Report($"Несовместимы с ядром и удалены: {skipped.Count}");
            AppLog.Write("core", $"Несовместимы с ядром и удалены: {skipped.Count}");
            nodes = nodes.Where(node => !skipped.Contains(node)).ToList();
        }

        // Конфиг мог не принять часть узлов (несовместимый формат): по ним нет тега
        // в ядре, и проба упала бы мгновенно. Такие узлы не считаем провалившимися.
        var loaded = nodes.Where(node => _loadedTags.Contains(node.Tag)).ToList();
        return await TestWithApiAsync(
            loaded, progress, onNodeTested, cancellationToken,
            concurrency, timeoutMs, youtubeUrl, telegramUrl);
    }

    /// <summary>
    /// Проверка только узлов, уже загруженных в работающее ядро: без перезапуска ядра,
    /// без пересборки конфига и без TUN-перехвата — не мешает другим приложениям.
    /// </summary>
    public async Task<TestSummary> TestLoadedNodesAsync(
        IReadOnlyList<ProxyNode> nodes,
        IProgress<string>? progress,
        Action<ProxyNode>? onNodeTested,
        CancellationToken cancellationToken,
        int concurrency = 2,
        int timeoutMs = 2500,
        string? youtubeUrl = null,
        string? telegramUrl = null)
    {
        var loaded = nodes.Where(node => _loadedTags.Contains(node.Tag)).ToList();
        if (loaded.Count == 0)
        {
            return new TestSummary(0, 0, 0, 0);
        }

        return await TestWithApiAsync(
            loaded, progress, onNodeTested, cancellationToken,
            concurrency, timeoutMs, youtubeUrl, telegramUrl);
    }

    private async Task<TestSummary> TestWithApiAsync(
        IReadOnlyList<ProxyNode> nodes,
        IProgress<string>? progress,
        Action<ProxyNode>? onNodeTested,
        CancellationToken cancellationToken,
        int concurrency,
        int timeoutMs,
        string? youtubeUrl,
        string? telegramUrl)
    {
        var api = _api ?? throw new InvalidOperationException("Clash API не запущен");
        var youtubeTarget = youtubeUrl ?? YoutubeTestUrl;
        IReadOnlyList<string> telegramTargets = string.IsNullOrWhiteSpace(telegramUrl)
            ? TelegramTestUrls
            : new[] { telegramUrl };

        var working = 0;
        var youtubeOk = 0;
        var telegramOk = 0;
        var done = 0;

        // Снимок состояния: если ни один узел не открылся, значит проблема в сети/ядре,
        // а не в 6 000 узлах — откатываем, чтобы не удалить весь список.
        var snapshots = new Dictionary<ProxyNode, (NodeStatus Status, int? DelayMs, DateTime? CheckedAtUtc,
            bool? YoutubeOk, bool? TelegramOk, int FailCount)>();
        foreach (var node in nodes)
        {
            // Один и тот же объект мог попасть в список дважды — Dictionary не терпит повторов.
            snapshots.TryAdd(node, (node.Status, node.DelayMs, node.CheckedAtUtc,
                node.YoutubeOk, node.TelegramOk, node.FailCount));
        }

        // Скорость проверки подстраивается под ситуацию: стартуем скромно (8 узлов
        // одновременно), а когда видно, что узлы живые, добавляем параллельность. Так слабый
        // канал и слабая машина не душат сами себя, а хороший список проверяется быстро.
        var limit = concurrency > 0 ? concurrency : 32;
        var start = Math.Min(limit, 8);
        using var limiter = new SemaphoreSlim(start);
        var ramp = 0;
        var tasks = nodes.Select(async node =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                // Два направления проверяем параллельно: вдвое быстрее при том же лимите узлов.
                // Telegram пробуем по нескольким доменам — хватает первого ответившего.
                var youtubeTask = api.TestDelayAsync(node.Tag, timeoutMs, cancellationToken, youtubeTarget);
                var telegramTask = TestAnyDelayAsync(api, node.Tag, telegramTargets, timeoutMs, cancellationToken);
                await Task.WhenAll(youtubeTask, telegramTask);
                var youtubeDelay = await youtubeTask;
                var telegramDelay = await telegramTask;

                node.YoutubeOk = youtubeDelay is not null;
                node.TelegramOk = telegramDelay is not null;
                node.CheckedAtUtc = DateTime.UtcNow;
                node.DelayMs = youtubeDelay ?? telegramDelay;

                if (youtubeDelay is null && telegramDelay is null)
                {
                    // Провал не удаляет узел сразу: считаем и убираем только после
                    // NodeLimits.RemoveAfterFailures провалов подряд.
                    NodeHealth.RegisterFailure(node);
                }
                else
                {
                    NodeHealth.RegisterSuccess(node);
                    Interlocked.Increment(ref working);
                }

                if (youtubeDelay is not null)
                {
                    Interlocked.Increment(ref youtubeOk);
                }

                if (telegramDelay is not null)
                {
                    Interlocked.Increment(ref telegramOk);
                }

                onNodeTested?.Invoke(node);
            }
            finally
            {
                limiter.Release();
                var finished = Interlocked.Increment(ref done);

                // Плавный разгон: если первые 40 узлов в основном живые, добавляем параллельность.
                if (finished == 40 && limit > start &&
                    Interlocked.CompareExchange(ref ramp, 1, 0) == 0 &&
                    Volatile.Read(ref working) * 100 / finished >= 35)
                {
                    limiter.Release(Math.Min(16, limit - start));
                }
                else if (finished == 120 && limit > start + 16 &&
                         Interlocked.CompareExchange(ref ramp, 2, 1) == 0 &&
                         Volatile.Read(ref working) * 100 / finished >= 55)
                {
                    limiter.Release(limit - start - 16);
                }

                if (finished % 25 == 0 || finished == nodes.Count)
                {
                    progress?.Report(
                        $"Проверено {finished} из {nodes.Count}: YouTube {Volatile.Read(ref youtubeOk)}, " +
                        $"Telegram {Volatile.Read(ref telegramOk)}");
                }
            }
        });

        await Task.WhenAll(tasks);

        if (working == 0 && nodes.Count >= 20 && !await DirectInternetWorksAsync(cancellationToken))
        {
            foreach (var (node, snapshot) in snapshots)
            {
                node.Status = snapshot.Status;
                node.DelayMs = snapshot.DelayMs;
                node.CheckedAtUtc = snapshot.CheckedAtUtc;
                node.YoutubeOk = snapshot.YoutubeOk;
                node.TelegramOk = snapshot.TelegramOk;
                node.FailCount = snapshot.FailCount;
            }

            AppLog.Write("test",
                $"Откат проверки: ни один из {nodes.Count} узлов не открылся и прямой интернет недоступен");
            throw new InvalidOperationException(
                "Ни один узел не открылся, и прямой интернет тоже недоступен. " +
                "Проверьте сеть, ядро и обход DPI — статусы узлов не изменены.");
        }

        AppLog.Write("test", $"Проверка узлов: YouTube {youtubeOk}, Telegram {telegramOk}, рабочих {working} из {nodes.Count}");
        return new TestSummary(nodes.Count, youtubeOk, telegramOk, working);
    }

    /// <summary>
    /// Проверка узла по списку адресов. Первый адрес пробуем отдельно, остальные —
    /// параллельно: так мёртвый узел ждёт не три таймаута, а два.
    /// </summary>
    private static async Task<int?> TestAnyDelayAsync(
        ClashApiClient api,
        string tag,
        IReadOnlyList<string> urls,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (urls.Count == 0)
        {
            return null;
        }

        var first = await api.TestDelayAsync(tag, timeoutMs, cancellationToken, urls[0]);
        if (first is not null || urls.Count == 1)
        {
            return first;
        }

        var rest = await Task.WhenAll(urls.Skip(1).Select(url =>
            api.TestDelayAsync(tag, timeoutMs, cancellationToken, url)));
        var working = rest.Where(delay => delay is not null).ToList();
        return working.Count == 0 ? null : working.Min();
    }

    /// <summary>Прямой HTTPS без прокси: нужен, чтобы отличить «все узлы мертвы» от «нет интернета».</summary>
    private static async Task<bool> DirectInternetWorksAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(4),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await client.GetAsync(
                "https://cp.cloudflare.com/generate_204",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                             or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task ConnectAsync(
        ProxyNode? node,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? probeUrls = null)
    {
        if (node is null)
        {
            throw new InvalidOperationException("Не выбран узел");
        }

        _probeUrls = probeUrls;

        if (!_runner.IsRunning || !_loadedTags.Contains(node.Tag))
        {
            var nodes = _loadedNodes.Count > 0 ? _loadedNodes.Append(node).ToList() : new List<ProxyNode> { node };
            await EnsureTestCoreAsync(nodes, progress, cancellationToken);
        }

        var api = _api ?? throw new InvalidOperationException("Clash API не запущен");
        progress?.Report($"Переключение на «{node.Name}»…");
        try
        {
            await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, node.Tag, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException("Ядро не приняло выбранный узел. Обновите списки и попробуйте снова.", exception);
        }

        if (Settings.ApplySystemProxy && Settings.TunnelMode != TunnelMode.Tun)
        {
            if (!_proxyApplied)
            {
                // Запоминаем прежние настройки до первого изменения, чтобы вернуть их как было.
                _proxyBackup = SystemProxy.Capture();
            }

            SystemProxy.Apply(ActiveMixedPort, _proxyBackup, Settings.ProxyBypass);
            _proxyApplied = true;
        }

        CurrentNode = node;
        IsConnected = true;
        IsKillSwitchHeld = false;
        _connectedAtUtc = DateTime.UtcNow;
        _nextHealthCheckUtc = DateTime.UtcNow.AddSeconds(60);
        _healthFailures = 0;
        StartHealthTimer();
        StartTrafficStats();
        AppLog.Write("proxy", $"Подключено: {node.Name} ({node.Protocol}, {node.Server}:{node.Port})");
        StateChanged?.Invoke($"Подключено: {node.Name}");
    }

    /// <summary>
    /// Переключение на уже загруженный узел без перезапуска ядра.
    /// Возвращает false, если узел не входит в текущий конфиг — тогда нужно полное подключение.
    /// </summary>
    public async Task<bool> SwitchToAsync(ProxyNode node, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!_runner.IsRunning || _api is null || !_loadedTags.Contains(node.Tag))
        {
            return false;
        }

        progress?.Report($"Переключаюсь на «{node.Name}»…");
        try
        {
            await _api.SelectAsync(SingBoxConfigBuilder.SelectorTag, node.Tag, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            AppLog.Write("proxy", $"Ядро не приняло узел «{node.Name}»", exception);
            return false;
        }

        CurrentNode = node;
        IsConnected = true;
        IsKillSwitchHeld = false;
        _connectedAtUtc = DateTime.UtcNow;
        _nextHealthCheckUtc = DateTime.UtcNow.AddSeconds(60);
        AppLog.Write("proxy", $"Переключено без перезапуска ядра: {node.Name} ({node.Protocol}, {node.Server}:{node.Port})");
        StateChanged?.Invoke($"Подключено: {node.Name}");
        return true;
    }

    public async Task DisconnectAsync()
    {
        StopHealthTimer();
        StopTrafficStats();
        if (_proxyApplied)
        {
            SystemProxy.Restore(_proxyBackup);
            _proxyApplied = false;
        }

        await _runner.StopAsync();
        _api?.Dispose();
        _api = null;
        _loadedSignature = string.Empty;
        IsConnected = false;
        IsKillSwitchHeld = false;
        CurrentNode = null;
        _probeUrls = null;
        DeleteActiveConfig();
        AppLog.Write("proxy", "Прокси отключён");
        StateChanged?.Invoke("Отключено");
    }

    /// <summary>Проверка, что через поднятый прокси действительно открывается сайт (например, YouTube).</summary>
    public async Task<bool> VerifyThroughProxyAsync(CancellationToken cancellationToken, string? url = null)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{ActiveMixedPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        try
        {
            using var response = await client.GetAsync(url ?? Settings.TestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // 4xx/5xx — это не «сайт открылся»: заглушка провайдера часто отдаёт 403/200.
            return (int)response.StatusCode < 400;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            AppLog.Write("proxy", "Проверка через прокси не прошла: " + exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Проверка через прокси по нескольким адресам: достаточно, чтобы открылся хотя бы один.
    /// Нужна для Telegram, у которого домены блокируются по отдельности.
    /// </summary>
    public async Task<bool> VerifyAnyThroughProxyAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<string>? urls = null)
    {
        foreach (var url in urls ?? new[] { Settings.TestUrl })
        {
            if (await VerifyThroughProxyAsync(cancellationToken, url))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Проверка одного узла на уже запущенном ядре.</summary>
    public async Task<int?> TestSingleAsync(ProxyNode node, CancellationToken cancellationToken)
    {
        if (_api is null)
        {
            return null;
        }

        var delay = await _api.TestDelayAsync(node.Tag, 5000, cancellationToken);
        node.DelayMs = delay;
        node.CheckedAtUtc = DateTime.UtcNow;
        return delay;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await _runner.DisposeAsync();
        _api?.Dispose();
    }

    /// <summary>
    /// Синхронное завершение при закрытии приложения: важно успеть вернуть системный прокси,
    /// иначе Windows останется без интернета после выхода.
    /// </summary>
    public void ShutdownSync()
    {
        StopHealthTimer();
        StopTrafficStats();
        try
        {
            if (_proxyApplied)
            {
                SystemProxy.Restore(_proxyBackup);
                _proxyApplied = false;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Write("proxy", "Не удалось вернуть настройки прокси при выходе", exception);
        }

        _runner.StopNow();
        _api?.Dispose();
        _api = null;
        IsConnected = false;
        IsKillSwitchHeld = false;
        CurrentNode = null;
        // В конфиге лежат UUID и пароли узлов: не оставляем их в папке после выхода.
        DeleteActiveConfig();
    }

    private async Task EnsureTestCoreAsync(IReadOnlyList<ProxyNode> nodes, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        _incompatible.Clear();
        // Загружаем в ядро все узлы партии, а не только лимит конфига: иначе узлы
        // сверх лимита не попадут в конфиг, проба для них сразу упадёт и узел
        // получит ложный провал. Лимит «узлов в конфиге» действует при подключении.
        var cap = Math.Max(Settings.MaxNodesInConfig, nodes.Count);
        var ordered = OrderForConfig(nodes)
            .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(cap)
            .ToList();
        // Маршрутизация тоже входит в подпись: смена TUN/правил должна пересобрать конфиг.
        var routingSignature = string.Join("|",
            Settings.TunnelMode,
            Settings.TunStrictRoute,
            Settings.SiteRouteMode,
            Settings.SiteList,
            Settings.OnlySelectedApps,
            Settings.AppList,
            Settings.DefaultSni,
            Settings.TlsFragment,
            Settings.TlsRecordFragment);
        var signature = string.Join(",", ordered.Select(n => n.Tag)) +
                        "|" + Settings.MixedPort + "|" + Settings.ApiPort + "|" + routingSignature;
        if (_runner.IsRunning && signature == _loadedSignature && _api is not null)
        {
            return;
        }

        await SingBoxCore.EnsureAsync(progress, cancellationToken);

        _mixedPort = FindFreePort(Settings.MixedPort);
        _apiPort = FindFreePort(Math.Max(Settings.ApiPort, _mixedPort + 1));
        _apiSecret = Guid.NewGuid().ToString("N");

        var json = SingBoxConfigBuilder.Build(ordered, ordered.FirstOrDefault()?.Tag, _mixedPort, _apiPort, _apiSecret, Settings);
        SingBoxConfigBuilder.WriteToFile(AppPaths.ActiveConfigPath, json);
        try
        {
            await SingBoxRunner.CheckConfigAsync(AppPaths.ActiveConfigPath, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // Один несовместимый узел не должен ломать весь список: находим и выбрасываем такие узлы.
            AppLog.Write("core", "Конфиг не прошёл проверку, исключаю несовместимые узлы", exception);
            progress?.Report("Убираю несовместимые с sing-box узлы…");
            var filtered = await FilterBrokenNodesAsync(ordered, cancellationToken);
            if (filtered.Count == 0)
            {
                throw new InvalidOperationException("Все узлы оказались несовместимы с sing-box: " + exception.Message, exception);
            }

            ordered = filtered;
            _incompatible.AddRange(nodes.Where(node => !filtered.Contains(node)));
            json = SingBoxConfigBuilder.Build(ordered, ordered.FirstOrDefault()?.Tag, _mixedPort, _apiPort, _apiSecret, Settings);
            SingBoxConfigBuilder.WriteToFile(AppPaths.ActiveConfigPath, json);
            await SingBoxRunner.CheckConfigAsync(AppPaths.ActiveConfigPath, cancellationToken);
        }

        if (Settings.TunnelMode == TunnelMode.Tun)
        {
            await SingBoxCore.EnsureWintunAsync(progress, cancellationToken);
        }

        progress?.Report("Запуск ядра sing-box…");
        try
        {
            await _runner.StartAsync(AppPaths.ActiveConfigPath, _apiPort, TimeSpan.FromSeconds(25), cancellationToken);
        }
        catch (Exception exception) when (Settings.TunnelMode == TunnelMode.Tun && exception is InvalidOperationException or TimeoutException)
        {
            throw new InvalidOperationException(
                "TUN-режим не запустился. Нужны права администратора и свободный адаптер. " +
                "Запустите VieNLink от имени администратора или переключитесь на системный прокси. " +
                exception.Message,
                exception);
        }

        _api?.Dispose();
        _api = new ClashApiClient(_apiPort, _apiSecret);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await _api.PingAsync(cancellationToken))
            {
                break;
            }

            await Task.Delay(150, cancellationToken);
        }

        _loadedSignature = signature;
        _loadedNodes = ordered;
        _loadedTags.Clear();
        foreach (var tag in ordered.Select(n => n.Tag))
        {
            _loadedTags.Add(tag);
        }

        AppLog.Write("core", $"Ядро готово: mixed {_mixedPort}, API {_apiPort}");
    }

    /// <summary>Личные узлы первыми, затем маскированные и быстрые — так они попадают в конфиг и выбираются раньше.</summary>
    public IEnumerable<ProxyNode> OrderForConfig(IEnumerable<ProxyNode> nodes) =>
        nodes
            .OrderByDescending(n => n.IsMine)
            .ThenByDescending(NodeRanking.Score)
            .ThenByDescending(n => n.DelayMs is not null)
            .ThenBy(n => n.DelayMs ?? int.MaxValue)
            .ThenBy(n => n.Protocol)
            .ToList();

    private void StartHealthTimer()
    {
        StopHealthTimer();
        _healthTimer = new System.Threading.Timer(_ => _ = TickHealthAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Проверка, что через узел реально открывается сайт. Каждый запрос идёт через узел,
    /// то есть тратит его трафик: обычная пауза — 90 секунд, после сбоя — быстрее
    /// (25–120 с), чтобы автопереключение сработало без долгого простоя.
    /// </summary>
    private async Task TickHealthAsync()
    {
        try
        {
            if (!IsConnected)
            {
                return;
            }

            if (!_runner.IsRunning)
            {
                await HandleCrashAsync();
                return;
            }

            if ((DateTime.UtcNow - _connectedAtUtc).TotalSeconds < 20 || DateTime.UtcNow < _nextHealthCheckUtc)
            {
                return;
            }

            if (Interlocked.Exchange(ref _healthBusy, 1) == 1)
            {
                return;
            }

            try
            {
                var healthy = await VerifyAnyThroughProxyAsync(CancellationToken.None, _probeUrls);
                if (!healthy && IsConnected)
                {
                    _healthFailures++;
                    _nextHealthCheckUtc = DateTime.UtcNow.AddSeconds(Math.Min(120, 25 + _healthFailures * 12));
                    AppLog.Write("proxy",
                        $"Узел перестал открывать проверочный адрес (сбой {_healthFailures}) — нужно переключение");
                    NodeUnhealthy?.Invoke(CurrentNode?.Name ?? "узел");
                    return;
                }

                _healthFailures = 0;
                _nextHealthCheckUtc = DateTime.UtcNow.AddSeconds(90);
            }
            finally
            {
                Interlocked.Exchange(ref _healthBusy, 0);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("proxy", "Ошибка проверки состояния узла", exception);
        }
    }

    private void StopHealthTimer()
    {
        _healthTimer?.Dispose();
        _healthTimer = null;
    }

    private async Task HandleCrashAsync()
    {
        AppLog.Write("core", "sing-box неожиданно завершился — прокси отключён");
        StopHealthTimer();
        StopTrafficStats();

        // Kill switch: намеренно не возвращаем системный прокси. Он остаётся указывать на
        // закрытый локальный порт: сайты не откроются, но трафик не уйдёт напрямую.
        if (Settings.KillSwitch && _proxyApplied && Settings.TunnelMode != TunnelMode.Tun)
        {
            _api?.Dispose();
            _api = null;
            IsConnected = false;
            IsKillSwitchHeld = true;
            CurrentNode = null;
            AppLog.Write("proxy", "Kill switch: системный прокси оставлен на закрытом порту");
            StateChanged?.Invoke(
                "Kill switch: ядро остановилось, прямой трафик заблокирован. " +
                "Нажмите «Отключиться», чтобы вернуть интернет.");
            return;
        }

        try
        {
            await DisconnectAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write("core", "Ошибка при аварийном отключении", exception);
        }

        StateChanged?.Invoke("Ядро sing-box остановилось. Нажмите «Подключиться» ещё раз.");
    }

    public long TotalUp => Interlocked.Read(ref _totalUp);

    public long TotalDown => Interlocked.Read(ref _totalDown);

    /// <summary>Поток статистики: sing-box присылает байты за секунду, мы копим итог за сессию.</summary>
    private void StartTrafficStats()
    {
        StopTrafficStats();
        var api = _api;
        if (api is null)
        {
            return;
        }

        Interlocked.Exchange(ref _totalUp, 0);
        Interlocked.Exchange(ref _totalDown, 0);
        var cts = new CancellationTokenSource();
        _statsCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await api.StreamTrafficAsync((up, down) =>
                {
                    var totalUp = Interlocked.Add(ref _totalUp, up);
                    var totalDown = Interlocked.Add(ref _totalDown, down);
                    TrafficUpdated?.Invoke(new TrafficSample(up, down, totalUp, totalDown));
                }, token);
            }
            catch (OperationCanceledException)
            {
                // Отключились — поток закрыт штатно.
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                                                 or ObjectDisposedException or InvalidOperationException)
            {
                AppLog.Write("stats", "Поток статистики трафика прерван", exception);
            }
        }, token);
    }

    private void StopTrafficStats()
    {
        var cts = _statsCts;
        _statsCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        // Не освобождаем сразу: поток ещё может держать токен в незавершённом чтении.
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
    }

    /// <summary>
    /// Замер пропускной способности узла. Качает ~1 МБ через локальный mixed-порт:
    /// задержка не показывает скорость, а для видео важна именно она. Во время
    /// активного соединения замер разрешён только для текущего узла.
    /// </summary>
    public async Task<double?> MeasureSpeedAsync(ProxyNode node, CancellationToken cancellationToken, int targetBytes = 1_000_000)
    {
        if (!_runner.IsRunning || _api is null || !_loadedTags.Contains(node.Tag))
        {
            return null;
        }

        if (IsConnected && CurrentNode is { } current && current.Tag != node.Tag)
        {
            return null;
        }

        var api = _api;
        var previous = await api.GetSelectedAsync(SingBoxConfigBuilder.SelectorTag, cancellationToken);
        var switched = !string.IsNullOrEmpty(previous) && previous != node.Tag;
        if (switched)
        {
            await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, node.Tag, cancellationToken);
        }

        try
        {
            var urls = new[]
            {
                $"https://speed.cloudflare.com/__down?bytes={targetBytes}",
                "https://proof.ovh.net/files/1Mb.dat",
                // Зеркала внутри РФ: работают, когда Cloudflare и OVH заблокированы.
                "https://speedtest.selectel.ru/10MB",
            };
            foreach (var url in urls)
            {
                var mbps = await DownloadSpeedAsync(url, targetBytes, cancellationToken);
                if (mbps is not null)
                {
                    node.SpeedMbps = Math.Round(mbps.Value, 1);
                    return node.SpeedMbps;
                }
            }

            return null;
        }
        finally
        {
            if (switched && !string.IsNullOrEmpty(previous))
            {
                try
                {
                    await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, previous, CancellationToken.None);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    AppLog.Write("speed", "Не удалось вернуть прежний узел после замера", exception);
                }
            }
        }
    }

    private async Task<double?> DownloadSpeedAsync(string url, int targetBytes, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{ActiveMixedPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (total < targetBytes && stopwatch.Elapsed < TimeSpan.FromSeconds(8))
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            // Меньше 64 КБ — это страница-заглушка или обрыв, а не скорость канала.
            if (total < 64 * 1024)
            {
                return null;
            }

            return total * 8.0 / Math.Max(0.2, stopwatch.Elapsed.TotalSeconds) / 1_000_000.0;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                             or IOException)
        {
            return null;
        }
    }

    /// <summary>В конфиге ядра лежат UUID и пароли узлов — не оставляем их в папке после выхода.</summary>
    private static void DeleteActiveConfig()
    {
        try
        {
            if (File.Exists(AppPaths.ActiveConfigPath))
            {
                File.Delete(AppPaths.ActiveConfigPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("core", "Не удалось удалить конфиг ядра", exception);
        }
    }

    /// <summary>
    /// Ищет узлы, из-за которых sing-box не принимает конфиг, делением списка пополам.
    /// Проверка запускается только когда обычный конфиг уже не прошёл, поэтому редкая.
    /// </summary>
    private async Task<List<ProxyNode>> FilterBrokenNodesAsync(List<ProxyNode> nodes, CancellationToken cancellationToken)
    {
        var result = new List<ProxyNode>(nodes.Count);
        var queue = new Queue<List<ProxyNode>>();
        queue.Enqueue(nodes);

        var checks = 0;
        var path = Path.Combine(AppPaths.RuntimeDirectory, "check-config.json");
        while (queue.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            if (checks >= 80)
            {
                AppLog.Write("core", "Проверка конфига остановлена по лимиту, часть узлов отброшена");
                break;
            }

            var batch = queue.Dequeue();
            if (batch.Count == 0)
            {
                continue;
            }

            var json = SingBoxConfigBuilder.Build(batch, batch[0].Tag, Settings.MixedPort, Settings.ApiPort, "check", Settings);
            SingBoxConfigBuilder.WriteToFile(path, json);
            checks++;
            try
            {
                await SingBoxRunner.CheckConfigAsync(path, cancellationToken);
                result.AddRange(batch);
            }
            catch (InvalidOperationException)
            {
                if (batch.Count == 1)
                {
                    AppLog.Write("core", "Узел исключён из конфига: " + batch[0].Name);
                    continue;
                }

                var half = batch.Count / 2;
                queue.Enqueue(batch.Take(half).ToList());
                queue.Enqueue(batch.Skip(half).ToList());
            }
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Временный конфиг удалится при следующей проверке.
        }

        AppLog.Write("core", $"Проверок конфига: {checks}, осталось узлов {result.Count} из {nodes.Count}");
        return result;
    }

    private static int FindFreePort(int preferred)
    {
        for (var port = preferred; port < preferred + 40; port++)
        {
            if (IsPortFree(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("Нет свободного локального порта рядом с " + preferred);
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

internal readonly record struct TestSummary(int Total, int YoutubeOk, int TelegramOk, int Working);
