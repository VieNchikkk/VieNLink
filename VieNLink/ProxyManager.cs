using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace VieNLink;

/// <summary>
/// Управляет жизненным циклом ядра: проверка серверов через Clash API, подключение,
/// системный прокси и контроль, что sing-box не упал.
/// </summary>
internal sealed class ProxyManager : IAsyncDisposable
{
    /// <summary>Универсальный адрес: доступ к Cloudflare — главный признак годности сервера.</summary>
    public const string UniversalTestUrl = "https://cp.cloudflare.com/generate_204";

    /// <summary>
    /// Заблокированные домены для оценки сервера: урезанный список проверки
    /// (`TlsHello.VerificationDomains`). Чем больше открывается — тем лучше сервер.
    /// </summary>
    public static IReadOnlyList<string> BlockedTestDomains => TlsHello.VerificationDomains;

    /// <summary>Проверочные адреса: сервер считается рабочим, если открывает хотя бы один.</summary>
    public const string YoutubeTestUrl = "https://www.youtube.com/";

    public const string TelegramTestUrl = "https://t.me/";

    public const string DiscordTestUrl = "https://discord.com/";

    /// <summary>
    /// Telegram живёт на нескольких доменах: мессенджер, веб-версия и сайт. Сервер получает
    /// отметку «Telegram», если открылся хотя бы один — иначе рабочий сервер браковался бы
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

    /// <summary>Серверы, которые sing-box не принял (несовместимый формат): их нужно удалить из списка.</summary>
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

    /// <summary>Сервер уже загружен в работающее ядро — переключение на него не требует перезапуска.</summary>
    public bool IsNodeLoaded(ProxyNode node) => _loadedTags.Contains(node.Tag);

    public ProxyNode? CurrentNode { get; private set; }

    public int ActiveMixedPort => _mixedPort > 0 ? _mixedPort : Settings.MixedPort;

    /// <summary>Серверы, которые сейчас загружены в работающее ядро.</summary>
    public IReadOnlyList<ProxyNode> LoadedNodes => _loadedNodes;

    /// <summary>Ядро упало или прокси отключён извне — UI должен обновить состояние.</summary>
    public event Action<string>? StateChanged;

    /// <summary>Сервер подключён, но перестал открывать проверочный адрес — пора переключаться.</summary>
    /// <summary>Сервер перестал открывать проверочный адрес. Передаётся сам сервер, чтобы
    /// обработчик не принял событие за уже сменившийся вручную сервер.</summary>
    public event Action<ProxyNode>? NodeUnhealthy;

    /// <summary>Отсчёт статистики трафика: скорость и накопленный объём за сессию.</summary>
    public event Action<TrafficSample>? TrafficUpdated;

    public async Task EnsureCoreAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await SingBoxCore.EnsureAsync(progress, cancellationToken);
    }

    /// <summary>
    /// Поднимает ядро с указанными серверами без проверки — нужно для замера скорости,
    /// когда пользователь ещё не подключён.
    /// </summary>
    public Task PrepareTestCoreAsync(
        IReadOnlyList<ProxyNode> nodes,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        EnsureTestCoreAsync(nodes, progress, cancellationToken);

    /// <summary>
    /// Проверяет серверы по двум адресам (YouTube и Telegram) и заполняет отметки в сервере.
    /// Не открыл ни один — сервер остаётся в списке, пока не наберёт
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
        string? telegramUrl = null,
        bool fullDomains = true)
    {
        if (nodes.Count == 0)
        {
            return new TestSummary(0, 0, 0, 0);
        }

        await EnsureTestCoreAsync(nodes, progress, cancellationToken);

        // Серверы, которые sing-box не принял (например, чужой отпечаток uTLS), проверять
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

        // Конфиг мог не принять часть серверов (несовместимый формат): по ним нет тега
        // в ядре, и проба упала бы мгновенно. Такие серверы не считаем провалившимися.
        var loaded = nodes.Where(node => _loadedTags.Contains(node.Tag)).ToList();
        return await TestWithApiAsync(
            loaded, progress, onNodeTested, cancellationToken,
            concurrency, timeoutMs, youtubeUrl, telegramUrl, fullDomains);
    }

    /// <summary>
    /// Проверка только серверов, уже загруженных в работающее ядро: без перезапуска ядра,
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
        string? telegramUrl = null,
        bool fullDomains = true)
    {
        var loaded = nodes.Where(node => _loadedTags.Contains(node.Tag)).ToList();
        if (loaded.Count == 0)
        {
            return new TestSummary(0, 0, 0, 0);
        }

        return await TestWithApiAsync(
            loaded, progress, onNodeTested, cancellationToken,
            concurrency, timeoutMs, youtubeUrl, telegramUrl, fullDomains);
    }

    private async Task<TestSummary> TestWithApiAsync(
        IReadOnlyList<ProxyNode> nodes,
        IProgress<string>? progress,
        Action<ProxyNode>? onNodeTested,
        CancellationToken cancellationToken,
        int concurrency,
        int timeoutMs,
        string? youtubeUrl,
        string? telegramUrl,
        bool fullDomains)
    {
        var api = _api ?? throw new InvalidOperationException("Clash API не запущен");
        return await TestWithApiAsync(
            api, nodes, progress, onNodeTested, cancellationToken,
            concurrency, timeoutMs, youtubeUrl, telegramUrl, fullDomains);
    }

    /// <summary>
    /// Общая проверка через готовый Clash API: используется и основным ядром,
    /// и скрытым пробным туннелем (отдельный процесс, свои порты).
    /// </summary>
    internal static async Task<TestSummary> TestWithApiAsync(
        ClashApiClient api,
        IReadOnlyList<ProxyNode> nodes,
        IProgress<string>? progress,
        Action<ProxyNode>? onNodeTested,
        CancellationToken cancellationToken,
        int concurrency,
        int timeoutMs,
        string? youtubeUrl,
        string? telegramUrl,
        bool fullDomains = true)
    {
        var youtubeTarget = youtubeUrl ?? YoutubeTestUrl;
        IReadOnlyList<string> telegramTargets = string.IsNullOrWhiteSpace(telegramUrl)
            ? TelegramTestUrls
            : new[] { telegramUrl };

        var working = 0;
        var youtubeOk = 0;
        var telegramOk = 0;
        var bestDomains = 0;
        var done = 0;
        // Отдельно считаем ранее рабочие серверы: если ни один из них не открылся,
        // это сбой проверки/сети, а не смерть всех сразу.
        var workingTested = 0;
        var workingSucceeded = 0;
        var failedWorking = new List<ProxyNode>();

        // Снимок состояния: если ни один сервер не открылся, значит проблема в сети/ядре,
        // а не в 6 000 серверах — откатываем, чтобы не удалить весь список.
        var snapshots = new Dictionary<ProxyNode, (NodeStatus Status, int? DelayMs, DateTime? CheckedAtUtc,
            bool? YoutubeOk, bool? TelegramOk, bool? UniversalOk, bool? DiscordOk,
            int OpenDomains, int FailCount)>();
        foreach (var node in nodes)
        {
            // Один и тот же объект мог попасть в список дважды — Dictionary не терпит повторов.
            snapshots.TryAdd(node, (node.Status, node.DelayMs, node.CheckedAtUtc,
                node.YoutubeOk, node.TelegramOk, node.UniversalOk, node.DiscordOk,
                node.OpenDomains, node.FailCount));
        }

        // Скорость проверки подстраивается под ситуацию: стартуем с 10 серверов
        // одновременно, а когда видно, что серверы живые, добавляем параллельность. Так слабый
        // канал и слабая машина не душат сами себя, а хороший список проверяется быстро.
        var limit = concurrency > 0 ? concurrency : 32;
        var start = Math.Min(limit, 10);
        using var limiter = new SemaphoreSlim(start);
        var ramp = 0;
        var tasks = nodes.Select(async node =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                // Статус до проверки: рабочему серверу даём вторую попытку, чтобы одна
                // ошибка сети (или занятость бесплатного сервера) не убирала его из счётчиков.
                var wasWorking = node.Status == NodeStatus.Working;
                if (wasWorking)
                {
                    Interlocked.Increment(ref workingTested);
                }

                // Отметка попытки: после провала рабочий сервер не должен снова
                // попасть в следующую партию раньше NodeLimits.WorkingRetryMinutes.
                node.TriedAtUtc = DateTime.UtcNow;

                // Рабочему серверу — больше времени: он уже открывал адреса,
                // а под нагрузкой сети ответ приходит медленнее.
                var probeTimeout = wasWorking ? Math.Max(timeoutMs, 4500) : timeoutMs;

                // Три направления проверяем параллельно: универсальный Cloudflare — главный
                // признак годности, YouTube и Telegram — отметки сервисов. Telegram пробуем
                // по нескольким доменам — хватает первого ответившего.
                var universalTask = api.TestDelayAsync(node.Tag, probeTimeout, cancellationToken, UniversalTestUrl);
                var youtubeTask = api.TestDelayAsync(node.Tag, probeTimeout, cancellationToken, youtubeTarget);
                var telegramTask = TestAnyDelayAsync(api, node.Tag, telegramTargets, probeTimeout, cancellationToken);
                await Task.WhenAll(universalTask, youtubeTask, telegramTask);
                var universalDelay = await universalTask;
                var youtubeDelay = await youtubeTask;
                var telegramDelay = await telegramTask;

                if (universalDelay is null && youtubeDelay is null && telegramDelay is null && wasWorking)
                {
                    await Task.Delay(600, cancellationToken).ConfigureAwait(false);
                    universalTask = api.TestDelayAsync(node.Tag, probeTimeout, cancellationToken, UniversalTestUrl);
                    youtubeTask = api.TestDelayAsync(node.Tag, probeTimeout, cancellationToken, youtubeTarget);
                    telegramTask = TestAnyDelayAsync(api, node.Tag, telegramTargets, probeTimeout, cancellationToken);
                    await Task.WhenAll(universalTask, youtubeTask, telegramTask);
                    universalDelay = await universalTask;
                    youtubeDelay = await youtubeTask;
                    telegramDelay = await telegramTask;
                }

                if (universalDelay is null && youtubeDelay is null && telegramDelay is null)
                {
                    // Провал не трогает результат последней удачной проверки и не удаляет
                    // сервер сразу: считаем провалы и убираем только после
                    // NodeLimits.RemoveAfterFailures подряд. Для рабочего сервера провал
                    // сначала подтверждается отдельной проверкой после партии.
                    if (!wasWorking)
                    {
                        node.CheckedAtUtc = DateTime.UtcNow;
                        node.YoutubeOk = false;
                        node.TelegramOk = false;
                        node.DelayMs = null;
                        node.UniversalOk = false;
                        node.DiscordOk = false;
                        node.OpenDomains = 0;
                        NodeHealth.RegisterFailure(node);
                    }
                    else
                    {
                        lock (failedWorking)
                        {
                            failedWorking.Add(node);
                        }
                    }
                }
                else
                {
                    node.YoutubeOk = youtubeDelay is not null;
                    node.TelegramOk = telegramDelay is not null;
                    node.UniversalOk = universalDelay is not null;
                    node.CheckedAtUtc = DateTime.UtcNow;

                    // Задержка — по YouTube: видео — главный признак «хорошего» сервера,
                    // поэтому «Универсальный» и YouTube сортируются по скорости YouTube.
                    // Если YouTube не открылся, берём Cloudflare, затем Telegram.
                    node.DelayMs = youtubeDelay ?? universalDelay ?? telegramDelay;

                    // Полный список заблокированных доменов прогоняем только у живого
                    // сервера и только когда он нужен (поиск новых/кнопка): в фоновых
                    // перепроверках достаточно базовой проверки, чтобы не тратить
                    // таймауты на домены. Мёртвым это таймауты впустую.
                    if (fullDomains)
                    {
                        var (openDomains, discordOk) = await ProbeBlockedDomainsAsync(
                            api, node.Tag, timeoutMs, cancellationToken).ConfigureAwait(false);
                        node.OpenDomains = openDomains;
                        node.DiscordOk = discordOk;
                        RaiseMax(ref bestDomains, openDomains);
                    }

                    NodeHealth.RegisterSuccess(node);
                    if (wasWorking)
                    {
                        Interlocked.Increment(ref workingSucceeded);
                    }

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

                // Плавный разгон после первых 40 серверов. Раньше он включался, только
                // если живых среди них было не меньше 20 %, и партия из мёртвых адресов
                // так и висела на 10 потоках: 200 таймаутов по 3 с — это минуты.
                // Мёртвый сервер канал не занимает, поэтому разгоняемся всегда,
                // а от перегрузки защищает сам предел параллельности.
                if (finished == 40 && limit > start &&
                    Interlocked.CompareExchange(ref ramp, 1, 0) == 0)
                {
                    limiter.Release(Math.Min(16, limit - start));
                }
                else if (finished == 120 && limit > start + 16 &&
                         Interlocked.CompareExchange(ref ramp, 2, 1) == 0 &&
                         Volatile.Read(ref working) * 100 / finished >= 35)
                {
                    limiter.Release(limit - start - 16);
                }

                if (finished % 25 == 0 || finished == nodes.Count)
                {
                    // В шапке нужен только ход проверки: разбивка по сервисам есть
                    // в итоговой строке и в журнале. Пишем «партия», а не «проверено»:
                    // иначе строка читается как общий итог и спорит со щитом в шапке
                    // (там доступные серверы, а здесь — открывшие адрес в этой партии).
                    progress?.Report(
                        $"Партия: {finished} из {nodes.Count} · открыли адрес {Volatile.Read(ref working)}");
                }
            }
        });

        await Task.WhenAll(tasks);

        // Рабочие серверы, не открывшие адреса в общей партии, проверяем ещё раз
        // поодиночке, без нагрузки: партия сама даёт ложные провалы. Только после
        // этой проверки растёт FailCount.
        List<ProxyNode> failedWorkingNow;
        lock (failedWorking)
        {
            failedWorkingNow = failedWorking.Distinct().ToList();
        }

        if (failedWorkingNow.Count > 0)
        {
            var (recovered, stillFailed) = await ConfirmFailedWorkingAsync(
                api, failedWorkingNow, cancellationToken);

            foreach (var node in recovered)
            {
                Interlocked.Increment(ref working);
                Interlocked.Increment(ref workingSucceeded);
                if (node.YoutubeOk == true)
                {
                    Interlocked.Increment(ref youtubeOk);
                }

                if (node.TelegramOk == true)
                {
                    Interlocked.Increment(ref telegramOk);
                }
            }

            lock (failedWorking)
            {
                failedWorking.Clear();
                failedWorking.AddRange(stillFailed);
            }
        }

        var workingBefore = snapshots.Count(pair => pair.Value.Status == NodeStatus.Working);
        var testedWorking = Volatile.Read(ref workingTested);
        var succeededWorking = Volatile.Read(ref workingSucceeded);
        var massRollback = false;

        // Восстанавливает прежние отметки (в том числе FailCount) у рабочих серверов.
        // only = null — все рабочие из снимка; список — только конкретные провалившиеся.
        void RestoreFromSnapshots(IEnumerable<ProxyNode>? only)
        {
            var targets = only ?? snapshots.Keys;
            foreach (var node in targets)
            {
                if (!snapshots.TryGetValue(node, out var snapshot) ||
                    snapshot.Status != NodeStatus.Working)
                {
                    continue;
                }

                node.Status = snapshot.Status;
                node.DelayMs = snapshot.DelayMs;
                node.CheckedAtUtc = snapshot.CheckedAtUtc;
                node.YoutubeOk = snapshot.YoutubeOk;
                node.TelegramOk = snapshot.TelegramOk;
                node.UniversalOk = snapshot.UniversalOk;
                node.DiscordOk = snapshot.DiscordOk;
                node.OpenDomains = snapshot.OpenDomains;
                node.FailCount = snapshot.FailCount;
            }
        }

        // Ни один из ранее рабочих серверов не открылся — это сбой проверки/сети,
        // а не смерть всех сразу. Порог 3: пара действительно мёртвых серверов
        // должна дойти до RemoveAfterFailures, иначе откат будет вечно возвращать их FailCount.
        if (testedWorking >= 3 && succeededWorking == 0 &&
            await DirectInternetWorksAsync(cancellationToken))
        {
            lock (failedWorking)
            {
                RestoreFromSnapshots(failedWorking);
            }

            massRollback = true;
            AppLog.Write("probe",
                $"Рабочие серверы разом не открылись ({testedWorking}) — провалы не засчитаны");
        }

        // Все серверы, которые уже были рабочими, разом не открылись — это почти наверняка
        // проблема проверки (DNS, сеть, пробное ядро), а не смерть всех серверов.
        // Оставляем их рабочими: иначе одна плохая партия отправляла живые в отсев.
        if (!massRollback && working == 0 && workingBefore >= 3 &&
            await DirectInternetWorksAsync(cancellationToken))
        {
            RestoreFromSnapshots(null);
            AppLog.Write("probe",
                $"Массовый провал: все {workingBefore} рабочих серверов не открылись — статусы не изменены");
        }

        if (!massRollback && working == 0 && nodes.Count > 0 &&
            !await DirectInternetWorksAsync(cancellationToken))
        {
            RestoreFromSnapshots(null);
            AppLog.Write("test",
                $"Откат проверки: ни один из {nodes.Count} серверов не открылся и прямой интернет недоступен");
            throw new InvalidOperationException(
                "Ни один сервер не открылся, и прямой интернет тоже недоступен. " +
                "Проверьте сеть, ядро и обход DPI — статусы серверов не изменены.");
        }

        // Подтверждение и откат меняли статус рабочих серверов уже после того, как
        // onNodeTested отработал в партии (Dead либо восстановленный FailCount). Сообщаем
        // UI ещё раз — иначе мёртвый сервер остаётся «рабочим» в списке и не попадает в отсев.
        List<ProxyNode> finalized;
        lock (failedWorking)
        {
            finalized = failedWorking.Distinct().ToList();
        }

        foreach (var node in finalized)
        {
            onNodeTested?.Invoke(node);
        }

        if (massRollback)
        {
            AppLog.Write("test",
                $"Проверка серверов: рабочие разом не открылись, провалы не засчитаны, " +
                $"рабочих {workingBefore + working} из {nodes.Count}");
            return new TestSummary(nodes.Count, youtubeOk, telegramOk, workingBefore + working, bestDomains);
        }

        AppLog.Write("test",
            $"Проверка серверов: YouTube {youtubeOk}, Telegram {telegramOk}, " +
            $"лучший открывает доменов {bestDomains}, рабочих {working} из {nodes.Count}");
        return new TestSummary(nodes.Count, youtubeOk, telegramOk, working, bestDomains);
    }

    /// <summary>
    /// Подтверждает провал рабочего сервера отдельной проверкой после партии.
    /// Проверяем YouTube, затем Telegram по доменам: если сервер открыл хотя бы один,
    /// провал не засчитывается. Только после этой проверки растёт FailCount.
    /// </summary>
    private static async Task<(List<ProxyNode> Recovered, List<ProxyNode> Failed)> ConfirmFailedWorkingAsync(
        ClashApiClient api,
        IReadOnlyList<ProxyNode> nodes,
        CancellationToken cancellationToken)
    {
        var recovered = new List<ProxyNode>();
        var failed = new List<ProxyNode>();

        AppLog.Write("test", $"Подтверждаю провал {nodes.Count} рабочих серверов");
        using var limiter = new SemaphoreSlim(10);
        var tasks = nodes.Select(async node =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                // Две попытки подтверждения: разовая заминка сети или занятость сервера
                // не должны засчитываться как провал (как на Android).
                var hit = await ConfirmOnceAsync(api, node.Tag, cancellationToken);
                if (hit is null && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(800, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Партию остановили во время паузы: провал не засчитываем.
                    }

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        hit = await ConfirmOnceAsync(api, node.Tag, cancellationToken);
                    }
                }

                if (hit is { } confirmed)
                {
                    node.CheckedAtUtc = DateTime.UtcNow;
                    node.TriedAtUtc = DateTime.UtcNow;
                    node.DelayMs = confirmed.Delay;
                    if (confirmed.Service == "Cloudflare")
                    {
                        node.UniversalOk = true;
                    }
                    else if (confirmed.Service == "YouTube")
                    {
                        node.YoutubeOk = true;
                    }
                    else
                    {
                        node.TelegramOk = true;
                    }

                    NodeHealth.RegisterSuccess(node);
                    lock (recovered)
                    {
                        recovered.Add(node);
                    }

                    AppLog.Write("test",
                        $"«{node.Name}»: подтверждён после провала партии ({confirmed.Service}, {confirmed.Delay} мс)");
                }
                else
                {
                    node.TriedAtUtc = DateTime.UtcNow;
                    var failuresBefore = node.FailCount;
                    NodeHealth.RegisterFailure(node);
                    lock (failed)
                    {
                        failed.Add(node);
                    }

                    AppLog.Write("test",
                        $"«{node.Name}»: провал подтверждён — FailCount {node.FailCount} " +
                        $"(было {failuresBefore}), {RejectedNode.Describe(node)}");
                }
            }
            catch (OperationCanceledException)
            {
                // Партию остановили: провал не засчитываем.
            }
            finally
            {
                limiter.Release();
            }
        });

        await Task.WhenAll(tasks);
        return (recovered, failed);
    }

    /// <summary>
    /// Один раунд подтверждения: открылся ли у сервера Cloudflare, YouTube или Telegram.
    /// Адреса идут по одному — нагруженный канал даёт ложные провалы, поэтому всё последовательно.
    /// </summary>
    private static async Task<(string Service, int? Delay)?> ConfirmOnceAsync(
        ClashApiClient api,
        string tag,
        CancellationToken cancellationToken)
    {
        const int timeoutMs = 8000;

        var delay = await api.TestDelayAsync(tag, timeoutMs, cancellationToken, UniversalTestUrl);
        if (delay is not null)
        {
            return ("Cloudflare", delay);
        }

        delay = await api.TestDelayAsync(tag, timeoutMs, cancellationToken, YoutubeTestUrl);
        if (delay is not null)
        {
            return ("YouTube", delay);
        }

        foreach (var target in TelegramTestUrls)
        {
            delay = await api.TestDelayAsync(tag, timeoutMs, cancellationToken, target);
            if (delay is not null)
            {
                return ("Telegram", delay);
            }
        }

        return null;
    }

    /// <summary>
    /// Сколько заблокированных доменов открывает сервер. Универсальный адрес (Cloudflare)
    /// проверяется отдельно в основной партии — здесь только счётчик доменов и Discord.
    /// </summary>
    private static async Task<(int OpenDomains, bool DiscordOk)> ProbeBlockedDomainsAsync(
        ClashApiClient api,
        string tag,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var domains = TlsHello.VerificationDomains;

        // Не больше четырёх запросов одновременно на сервер: иначе параллельные соединения
        // перегружают слабый бесплатный сервер, и он ложно показывает ноль доменов.
        using var limiter = new SemaphoreSlim(4);
        var tasks = domains
            .Select(async domain =>
            {
                await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await api.TestDelayAsync(
                        tag, timeoutMs, cancellationToken, "https://" + domain + "/").ConfigureAwait(false);
                }
                finally
                {
                    limiter.Release();
                }
            })
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var open = 0;
        var discordIndex = Array.IndexOf(domains, "discord.com");
        for (var index = 0; index < tasks.Length; index++)
        {
            if (tasks[index].Result is not null)
            {
                open++;
            }
        }

        var discordOk = discordIndex >= 0 && tasks[discordIndex].Result is not null;
        return (open, discordOk);
    }

    /// <summary>Атомарно поднимает максимум: нужно для прогресса по потокам.</summary>
    private static void RaiseMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>
    /// Проверка сервера по списку адресов. Первый адрес пробуем отдельно, остальные —
    /// параллельно: так мёртвый сервер ждёт не три таймаута, а два.
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

    /// <summary>Прямой HTTPS без прокси: нужен, чтобы отличить «все серверы мертвы» от «нет интернета».</summary>
    private static Task<bool> DirectInternetWorksAsync(CancellationToken cancellationToken) =>
        NetworkHealth.HasDirectInternetAsync(cancellationToken);

    public async Task ConnectAsync(
        ProxyNode? node,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? probeUrls = null)
    {
        if (node is null)
        {
            throw new InvalidOperationException("Не выбран сервер");
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
            throw new InvalidOperationException("Ядро не приняло выбранный сервер. Обновите списки и попробуйте снова.", exception);
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
    /// Переключение на уже загруженный сервер без перезапуска ядра.
    /// Возвращает false, если сервер не входит в текущий конфиг — тогда нужно полное подключение.
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
            AppLog.Write("proxy", $"Ядро не приняло сервер «{node.Name}»", exception);
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

    /// <summary>Проверка одного сервера на уже запущенном ядре.</summary>
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

    /// <summary>
    /// Адреса проверки через поднятый прокси: универсальный Cloudflare и один сервис,
    /// который сервер уже открывал. Проверка сервера (в скрытом ядре) считает рабочим
    /// любой открывшийся адрес, поэтому проверка через прокси не должна браковать сервер,
    /// у которого заблокирован только один домен. Иначе рабочий сервер удалялся бы после
    /// нескольких подключений из-за чужого провала — например, когда провайдер режет YouTube.
    /// </summary>
    public static IReadOnlyList<string> VerificationUrls(ProxyNode node)
    {
        var urls = new List<string> { UniversalTestUrl };
        if (node.YoutubeOk == true)
        {
            urls.Add(YoutubeTestUrl);
        }
        else if (node.TelegramOk == true)
        {
            urls.Add(TelegramTestUrls[0]);
        }
        else if (node.DiscordOk == true)
        {
            urls.Add(DiscordTestUrl);
        }

        return urls;
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
        // В конфиге лежат UUID и пароли серверов: не оставляем их в папке после выхода.
        DeleteActiveConfig();
    }

    private async Task EnsureTestCoreAsync(IReadOnlyList<ProxyNode> nodes, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        _incompatible.Clear();
        // Загружаем в ядро все серверы партии, а не только лимит конфига: иначе серверы
        // сверх лимита не попадут в конфиг, проба для них сразу упадёт и сервер
        // получит ложный провал. Лимит «серверов в конфиге» действует при подключении.
        var cap = Math.Max(Settings.MaxNodesInConfig, nodes.Count);
        // Сортировка тысяч серверов (NodeRanking.Score) — в фоне: на UI-потоке это
        // заметно подвешивало окно при подключении и переключении.
        var ordered = await Task.Run(() => OrderForConfig(nodes)
            .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(cap)
            .ToList(), cancellationToken);
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
            // Один несовместимый сервер не должен ломать весь список: находим и выбрасываем такие серверы.
            AppLog.Write("core", "Конфиг не прошёл проверку, исключаю несовместимые серверы", exception);
            progress?.Report("Убираю несовместимые с sing-box серверы…");
            var filtered = await FilterBrokenNodesAsync(ordered, cancellationToken);
            if (filtered.Count == 0)
            {
                throw new InvalidOperationException("Все серверы оказались несовместимы с sing-box: " + exception.Message, exception);
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

    /// <summary>Личные серверы первыми, затем маскированные и быстрые — так они попадают в конфиг и выбираются раньше.</summary>
    public IEnumerable<ProxyNode> OrderForConfig(IEnumerable<ProxyNode> nodes) =>
        nodes
            .OrderByDescending(n => n.IsMine)
            .ThenByDescending(n => n.RankScore)
            .ThenByDescending(n => n.DelayMs is not null)
            .ThenByDescending(n => n.OpenDomains)
            .ThenBy(n => n.DelayMs ?? int.MaxValue)
            .ThenBy(n => n.Protocol)
            .ToList();

    private void StartHealthTimer()
    {
        StopHealthTimer();
        _healthTimer = new System.Threading.Timer(_ => _ = TickHealthAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Проверка, что через сервер реально открывается сайт. Каждый запрос идёт через сервер,
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
                // Запоминаем, какой сервер проверяли: пока шёл запрос, пользователь мог
                // нажать «Дальше» и выбрать другой. Сбой старого сервера не должен
                // удалять только что выбранный рабочий.
                var testedNode = CurrentNode;
                var healthy = await VerifyAnyThroughProxyAsync(CancellationToken.None, _probeUrls);
                if (!healthy && IsConnected && testedNode is not null &&
                    ReferenceEquals(CurrentNode, testedNode))
                {
                    _healthFailures++;
                    _nextHealthCheckUtc = DateTime.UtcNow.AddSeconds(Math.Min(120, 25 + _healthFailures * 12));
                    // Два провала подряд: одиночный таймаут на загруженном канале не должен
                    // дёргать подключение. Android использует то же правило.
                    if (_healthFailures < 2)
                    {
                        AppLog.Write("proxy",
                            $"Сервер не открыл проверочный адрес (сбой {_healthFailures}/2) — проверю ещё раз");
                        return;
                    }

                    _healthFailures = 0;
                    AppLog.Write("proxy",
                        "Сервер перестал открывать проверочный адрес (2 сбоя подряд) — нужно переключение");
                    NodeUnhealthy?.Invoke(testedNode);
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
            AppLog.Write("proxy", "Ошибка проверки состояния сервера", exception);
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
    /// Замер пропускной способности сервера. Качает ~1 МБ через локальный mixed-порт:
    /// задержка не показывает скорость, а для видео важна именно она. Во время
    /// активного соединения замер разрешён только для текущего сервера.
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
                    AppLog.Write("speed", "Не удалось вернуть прежний сервер после замера", exception);
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

    /// <summary>В конфиге ядра лежат UUID и пароли серверов — не оставляем их в папке после выхода.</summary>
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
    /// Ищет серверы, из-за которых sing-box не принимает конфиг, делением списка пополам.
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
                AppLog.Write("core", "Проверка конфига остановлена по лимиту, часть серверов отброшена");
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
                    AppLog.Write("core", "Сервер исключён из конфига: " + batch[0].Name);
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

        AppLog.Write("core", $"Проверок конфига: {checks}, осталось серверов {result.Count} из {nodes.Count}");
        return result;
    }

    internal static int FindFreePort(int preferred)
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
            using var listener = new TcpListener(IPAddress.Loopback, port);
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

internal readonly record struct TestSummary(
    int Total, int YoutubeOk, int TelegramOk, int Working, int BestDomains = 0);
