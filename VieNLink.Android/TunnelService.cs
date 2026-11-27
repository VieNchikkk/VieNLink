using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;

namespace VieNLink.Mobile;

public enum TunnelState
{
    Stopped = 0,

    /// <summary>Служба запущена, ядро поднимается: TUN ещё не готов.</summary>
    Starting = 1,

    Running = 2,
}

/// <summary>
/// Служба VPN: поднимает TUN и отдаёт его файловый дескриптор ядру libbox.
/// Ядро само применяет конфиг sing-box (TUN-инбаунд, маршруты, outbound'ы),
/// служба отвечает только за разрешение, fd и защиту собственных сокетов.
/// </summary>
[Service(Exported = true, Permission = "android.permission.BIND_VPN_SERVICE",
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[IntentFilter(new[] { "android.net.VpnService" })]
public sealed class TunnelService : VpnService
{
    private const int NotificationId = 1001;
    private const string ChannelId = "vienlink-tunnel";

    private static string? _nodeLabel;

    public const string ActionStart = "com.vienlink.app.START";
    public const string ActionStop = "com.vienlink.app.STOP";

    /// <summary>Автозапуск (загрузка, плитка, восстановление): конфиг собирается внутри службы.</summary>
    public const string ActionAuto = "com.vienlink.app.AUTO";

    /// <summary>TUN-обход без сервера: TUN → встроенный форвардер с фрагментацией TLS.</summary>
    public const string ActionBypass = "com.vienlink.app.BYPASS";

    /// <summary>Стратегия фрагментации TUN-обхода (имя <see cref="StreamFragmentMode"/>).</summary>
    public const string ExtraStrategy = "strategy";

    /// <summary>Готовый конфиг sing-box из <c>SingBoxConfigBuilder</c>; если нет — прямой выход.</summary>
    public const string ExtraConfig = "config";

    private const string SessionName = "VieNLink";
    private const string DefaultTunAddress = "172.19.0.1";

    /// <summary>
    /// Временный конфиг для проверки связки: TUN + прямой выход без сервера.
    /// Если туннель поднялся и интернет работает — ядро, fd и маршруты связаны верно.
    /// Сюда же будет подставляться конфиг из <c>SingBoxConfigBuilder</c>.
    /// </summary>
    private const string DirectTunConfig = """
    {
      "log": { "level": "warn" },
      "dns": {
        "servers": [
          { "type": "udp", "tag": "remote", "server": "1.1.1.1" }
        ],
        "final": "remote"
      },
      "inbounds": [
        {
          "type": "tun",
          "tag": "tun-in",
          "address": [ "172.19.0.1/30" ],
          "mtu": 1500,
          "auto_route": true,
          "stack": "gvisor"
        }
      ],
      "outbounds": [ { "type": "direct", "tag": "direct" } ],
      "route": { "final": "direct", "auto_detect_interface": true }
    }
    """;

    private static TunnelState _state = TunnelState.Stopped;

    /// <summary>
    /// Состояние туннеля. Время подключения обновляется здесь же: экраны показывают,
    /// сколько держится соединение, не заводя собственный таймер.
    /// </summary>
    public static TunnelState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            StartedAtUtc = value switch
            {
                TunnelState.Running => DateTime.UtcNow,
                TunnelState.Stopped => null,
                _ => StartedAtUtc,
            };
        }
    }

    /// <summary>Когда туннель перешёл в «работает»; null — не подключён. Для времени сессии в интерфейсе.</summary>
    public static DateTime? StartedAtUtc { get; private set; }

    /// <summary>
    /// Последняя причина, по которой туннель не поднялся: экраны пишут её вместо
    /// молчаливого «VPN не подключён». Сбрасывается при новом запуске и ручном отключении.
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Текущая служба для платформы libbox: из неё берутся TUN и protect.
    /// null — ядро работает в режиме проверки серверов, TUN не нужен.
    /// </summary>
    public static TunnelService? Active { get; private set; }

    /// <summary>
    /// Сервис, выбранный кнопкой на главном экране. Хранится в службе, а не в Activity:
    /// переживает пересоздание экрана и задаёт приоритет при переключении серверов.
    /// null — подключались к конкретному серверу, приоритета сервиса нет.
    /// </summary>
    internal static NodeStore.QuickService? ActiveService { get; set; }

    private static int _upgradeBusy;
    private static DateTime _lastUpgradeUtc;

    /// <summary>Сервер, на который уже не удалось вернуться: не долбим его каждую минуту.</summary>
    private static readonly Dictionary<string, DateTime> _upgradeRetryAfter = new(StringComparer.OrdinalIgnoreCase);

    private ParcelFileDescriptor? _tunDescriptor;
    private ConnectivityManager? _connectivity;
    private Libbox.IInterfaceUpdateListener? _interfaceListener;
    private DefaultNetworkCallback? _networkCallback;
    private bool _bypassMode;
    private static Timer? _healthTimer;
    private static int _healthFailures;
    /// <summary>Сервер, к которому относятся накопленные провалы health-check.</summary>
    private static string? _healthNodeId;
    private CancellationTokenSource? _autoTuneCts;
    private static DateTime _lastAutoTuneUtc;

    /// <summary>
    /// Последний конфиг ядра: по нему пересоздаётся VPN-сеть, если Android
    /// пометил частный DNS сломанным и приложения остались без резолва.
    /// </summary>
    private string? _lastConfigJson;
    private static Timer? _privateDnsTimer;
    private static int _privateDnsRestarts;
    private static DateTime _lastPrivateDnsRestartUtc = DateTime.MinValue;
    private static DateTime _lastPrivateDnsHintUtc = DateTime.MinValue;
    /// <summary>Текущая остановка вызвана нашей же проверкой DNS, а не пользователем.</summary>
    private static bool _privateDnsSelfRestart;

    /// <summary>Служба поднимает TUN-обход: проверка серверов в отдельном процессе недоступна.</summary>
    public static bool IsBypassMode { get; private set; }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        Active = this;

        if (intent?.Action == ActionStop)
        {
            StopTunnel();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // Уведомление обязательно: без foreground-службы Android убивает туннель в фоне.
        StartForegroundNotification();

        // Система перезапустила службу после остановки процесса: intent пустой.
        // Раньше в этом случае поднимался прямой TUN без сервера (интернет «есть»,
        // а прокси нет), а health-check не мог ничего переключить. Восстанавливаем
        // последний режим: TUN-обход или прокси-сервер.
        if (intent is null)
        {
            return RestoreAfterSystemRestart()
                ? StartCommandResult.Sticky
                : StartCommandResult.NotSticky;
        }

        if (intent?.Action == ActionBypass)
        {
            if (State == TunnelState.Running && !_bypassMode)
            {
                // Режимы взаимоисключающие: обход не поднимаем поверх прокси-сервера.
                AppLog.Write("android", "Обход не включён: работает прокси-сервер");
                return StartCommandResult.Sticky;
            }

            // Передаём имя режима, а не индекс: порядок списка в интерфейсе и в Core
            // может отличаться (RecordSplit показывается первым).
            var requested = intent.GetStringExtra(ExtraStrategy);
            var mode = Enum.TryParse<StreamFragmentMode>(requested, out var parsed)
                ? parsed
                : StreamFragmentMode.RecordSplit;
            StartBypass(mode);
            return StartCommandResult.Sticky;
        }

        if (State == TunnelState.Running && _bypassMode)
        {
            // Сервер не поднимаем поверх TUN-обхода.
            AppLog.Write("android", "Сервер не включён: работает TUN-обход");
            return StartCommandResult.Sticky;
        }

        _bypassMode = false; IsBypassMode = false;
        if (AndroidSettingsStore.Current.LastRunWasBypass)
        {
            AndroidSettingsStore.Current.LastRunWasBypass = false;
            AndroidSettingsStore.Save();
        }

        if (intent?.Action == ActionAuto)
        {
            // После загрузки человека нет: нельзя поднимать туннель на сервере, который
            // умер, пока телефон был выключен. BuildTunnelConfig выберет рабочий.
            var autoConfig = NodeStore.BuildTunnelConfig(out var autoError, preferWorking: true);
            if (autoConfig is null)
            {
                AppLog.Write("android", "Автоподключение невозможно: " + autoError);
                StopTunnel();
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            StartCore(autoConfig);
            return StartCommandResult.Sticky;
        }

        var config = intent?.GetStringExtra(ExtraConfig);
        StartCore(string.IsNullOrWhiteSpace(config) ? DirectTunConfig : config);
        return StartCommandResult.Sticky;
    }

    /// <summary>
    /// Восстанавливает режим после перезапуска службы системой: TUN-обход по сохранённой
    /// стратегии или прокси-сервер из кэша (с проверкой живости). Возвращает false, если
    /// поднимать нечего.
    /// </summary>
    private bool RestoreAfterSystemRestart()
    {
        try
        {
            NodeStore.EnsureLoaded();
            var settings = AndroidSettingsStore.Current;
            if (settings.LastRunWasBypass)
            {
                // Был включён TUN-обход — возвращаем его с сохранённой стратегией,
                // а не поднимаем прокси-сервер, которого в этом режиме не было.
                var mode = Enum.TryParse<StreamFragmentMode>(NetworkPresets.ResolvePage(this), out var parsed)
                    ? parsed
                    : StreamFragmentMode.Oob;
                AppLog.Write("android", "Служба перезапущена системой: восстанавливаю TUN-обход");
                StartBypass(mode);
                return true;
            }

            settings.LastRunWasBypass = false;
            AndroidSettingsStore.Save();

            var config = NodeStore.BuildTunnelConfig(out var error, preferWorking: true);
            if (config is null)
            {
                AppLog.Write("android", "Служба перезапущена системой, но подключать нечего: " + error);
                StopTunnel();
                StopSelf();
                return false;
            }

            AppLog.Write("android", "Служба перезапущена системой: восстанавливаю прокси-сервер");
            StartCore(config);
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write("android", "Не удалось восстановить туннель после перезапуска службы", exception);
            StopTunnel();
            StopSelf();
            return false;
        }
    }

    /// <summary>
    /// TUN-обход без прокси-сервера: TUN → встроенный SOCKS5-форвардер, который режет
    /// TLS ClientHello. Сокеты форвардера идут мимо VPN, потому что процесс исключён
    /// из туннеля на уровне <c>VpnService</c>.
    /// </summary>
    private void StartBypass(StreamFragmentMode mode)
    {
        _bypassMode = true; IsBypassMode = true;
        AndroidSettingsStore.Current.LastRunWasBypass = true;
        NetworkPresets.SetPage(this, mode.ToString());
        AndroidSettingsStore.Save();
        var strategy = TlsFragmentForwarder.Strategies.FirstOrDefault(item => item.Mode == mode)
                       ?? TlsFragmentForwarder.Strategies[0];
        AppLog.Write("tun-bypass", $"Сеть: {NetworkPresets.Describe(this)}");

        _ = Task.Run(async () =>
        {
            try
            {
                var videoMode = Enum.TryParse<StreamFragmentMode>(
                    NetworkPresets.GetVideo(this), out var parsedVideo)
                    ? parsedVideo
                    : StreamFragmentMode.None;
                var port = await ForwarderHost.StartAsync(
                    mode, SettingsStore.Current.TunBypassAllHosts, videoMode).ConfigureAwait(false);

                var settings = SettingsStore.Current;
                var config = SingBoxConfigBuilder.BuildTunBypass(
                    port,
                    CoreRuntime.ApiPort,
                    CoreRuntime.ApiSecret,
                    NetworkPresets.GetBlockQuic(this),
                    processName: null,
                    onlyProcess: null,
                    // На Android системный резолвер ядру недоступен. 1.1.1.1 у российских
                    // операторов часто заблокирован, поэтому по умолчанию Яндекс-DNS.
                    dnsServer: "77.88.8.8");

                StartCore(config);

                if (State == TunnelState.Running)
                {
                    _nodeLabel = "TUN-обход (бета): " + strategy.Name;
                    RefreshNotification();
                }

                AppLog.Write("tun-bypass", $"TUN-обход запущен: {strategy.Name}, форвардер {port}");
                ScheduleAutoTune();
            }
            catch (Exception exception)
            {
                AppLog.Write("tun-bypass", "Не удалось запустить TUN-обход", exception);
                try
                {
                    StopTunnel();
                }
                catch (Exception)
                {
                    // Служба могла уже остановиться.
                }

                StopSelf();
            }
        });
    }

    public override void OnRevoke()
    {
        // Система отобрала VPN (другое приложение или пользователь) — не пытаемся вернуть.
        StopTunnel();
        StopSelf();
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        StopTunnel();
        Active = null;
        base.OnDestroy();
    }

    /// <summary>Foreground-уведомление: без него Android убивает службу в фоне.</summary>
    private void StartForegroundNotification()
    {
        EnsureChannel();
        var notification = BuildNotification();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O ||
            GetSystemService(NotificationService) is not NotificationManager manager)
        {
            return;
        }

        var channel = new NotificationChannel(ChannelId, "Туннель", NotificationImportance.Low)
        {
            Description = "Состояние VPN-туннеля VieNLink",
        };
        manager.CreateNotificationChannel(channel);
    }

    /// <summary>
    /// Уведомление: текущий сервер, переход к списку серверов и отключение в один тап.
    /// Обновляется при смене сервера (<see cref="UpdateNodeLabel"/>).
    /// </summary>
    private Notification BuildNotification()
    {
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        var selected = NodeStore.Selected;
        var exit = NetworkProbe.CachedExit;
        var server = selected is null
            ? _nodeLabel
            : ShortServerName(selected) + " · " + selected.DisplayDelay +
              (exit is null ? string.Empty : " · " + exit.Flag + " " + exit.Country);
        var text = State == TunnelState.Running
            ? server ?? "Туннель работает"
            : "Подключаюсь…";

        builder
            .SetContentTitle("VieNLink")
            .SetContentText(text)
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetOngoing(true)
            .SetShowWhen(false);

        var open = PendingIntent.GetActivity(
            this, 1, new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (open is not null)
        {
            builder.SetContentIntent(open);
        }

        var next = PendingIntent.GetActivity(
            this, 4,
            new Intent(this, typeof(MainActivity))
                .PutExtra("nextNode", true)
                .AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (next is not null)
        {
            builder.AddAction(0, "Следующий", next);
        }

        var nodes = PendingIntent.GetActivity(
            this, 2, new Intent(this, typeof(NodesActivity)).AddFlags(ActivityFlags.NewTask),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (nodes is not null)
        {
            builder.AddAction(0, "Серверы", nodes);
        }

        var stop = PendingIntent.GetService(
            this, 3, new Intent(this, typeof(TunnelService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (stop is not null)
        {
            builder.AddAction(0, "Отключить", stop);
        }

        return builder.Build();
    }

    private void RefreshNotification()
    {
        try
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.Notify(NotificationId, BuildNotification());
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("VieNLink", "уведомление: " + exception.Message);
        }
    }

    /// <summary>Показывает в уведомлении выбранный сервер (вызывается при подключении и переключении).</summary>
    public static void UpdateNodeLabel(string? label)
    {
        _nodeLabel = label;
        NetworkProbe.Invalidate();
        Active?.RefreshNotification();
        TunnelWidget.Update(Android.App.Application.Context);
    }

    /// <summary>
    /// Обновляет уведомление и виджет, когда стал известен выходной IP/страна.
    /// Не сбрасывает NetworkProbe, в отличие от <see cref="UpdateNodeLabel"/>.
    /// </summary>
    internal static void RefreshServerInfo()
    {
        Active?.RefreshNotification();
        TunnelWidget.Update(Android.App.Application.Context);
    }

    internal static string ShortServerName(ProxyNode node)
    {
        var name = NodeDisplay.Name(node);
        return name.Length > 42 ? name[..41] + "…" : name;
    }

    /// <summary>
    /// Проверка живости выбранного сервера раз в 90 секунд. После двух провалов подряд
    /// при включённом автопереключении выбирается другой сервер, уже загруженный в ядро.
    /// </summary>
    private void StartHealthMonitor()
    {
        if (_bypassMode || _healthTimer is not null)
        {
            return;
        }

        _healthFailures = 0;
        _healthTimer = new System.Threading.Timer(_ => _ = HealthTickAsync(), null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(90));
    }

    private void StopHealthMonitor()
    {
        _healthTimer?.Dispose();
        _healthTimer = null;
        _healthFailures = 0;
    }

    /// <summary>
    /// Наблюдение за частным DNS (Private DNS, DoT). Android проверяет его один раз
    /// при создании VPN-сети; если проверка не прошла при холодном старте или на мёртвом
    /// сервере, в строгом режиме (например, <c>dns.google</c>) все имена перестают
    /// резолвиться до смены сети или настройки. Проверки VieNLink идут в процессе
    /// <c>:probe</c> вне VPN и этого не видят: сервер «рабочий», а приложения не работают.
    /// </summary>
    private void StartPrivateDnsMonitor()
    {
        StopPrivateDnsMonitor();
        _privateDnsTimer = new System.Threading.Timer(
            _ => _ = CheckPrivateDnsAsync(), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90));
    }

    private void StopPrivateDnsMonitor()
    {
        _privateDnsTimer?.Dispose();
        _privateDnsTimer = null;
    }

    private async Task CheckPrivateDnsAsync()
    {
        try
        {
            if (_bypassMode || State != TunnelState.Running || !CoreRuntime.IsRunning)
            {
                return;
            }

            // Отметка о подтверждённых адресах DoT есть только с Android 14.
            if (Build.VERSION.SdkInt < BuildVersionCodes.UpsideDownCake)
            {
                return;
            }

            var privateDns = FindStrictPrivateDns();
            if (privateDns is null)
            {
                // Частный DNS выключен или в мягком режиме — ему VPN не мешает.
                return;
            }

            if (privateDns.Value.Validated)
            {
                if (_privateDnsRestarts > 0)
                {
                    AppLog.Write("health",
                        $"Частный DNS {privateDns.Value.Name}: после переподключения проверка прошла");
                    _privateDnsRestarts = 0;
                }

                return;
            }

            if (DateTime.UtcNow - _lastPrivateDnsHintUtc > TimeSpan.FromMinutes(10))
            {
                _lastPrivateDnsHintUtc = DateTime.UtcNow;
                AppLog.Write("health",
                    $"Частный DNS {privateDns.Value.Name}: Android не подтвердил DoT через VPN — " +
                    "приложения остаются без имён");
            }

            // Пересоздаём VPN-сеть только на заведомо рабочем сервере: иначе повторная
            // проверка DoT снова упадёт, и мы лишь оборвём соединение.
            var selected = NodeStore.Selected;
            if (selected is null || !CoreRuntime.LoadedTags.Contains(selected.Tag))
            {
                return;
            }

            int? delay;
            using (var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret))
            {
                delay = await api.TestDelayAsync(
                        selected.Tag, 5000, CancellationToken.None,
                        "https://cp.cloudflare.com/generate_204")
                    .ConfigureAwait(false);
            }

            if (delay is null)
            {
                // Сервер не отвечает: health-check переключит его, после этого проверим снова.
                return;
            }

            var now = DateTime.UtcNow;
            if (_privateDnsRestarts >= 2 || now - _lastPrivateDnsRestartUtc < TimeSpan.FromMinutes(10))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_lastConfigJson))
            {
                return;
            }

            _privateDnsRestarts++;
            _lastPrivateDnsRestartUtc = now;
            Android.Util.Log.Info("VieNLink", $"private dns {privateDns.Value.Name}: пересоздаю VPN-сеть");
            AppLog.Write("health",
                "Пересоздаю VPN-сеть, чтобы Android заново проверил частный DNS");

            // Новую VPN-сеть создаёт только новый экземпляр VpnService: простого
            // перечитывания конфига недостаточно, сеть остаётся прежней.
            var context = Application.Context;
            var config = _lastConfigJson;
            _privateDnsSelfRestart = true;
            try
            {
                VpnStarter.Stop(context);
            }
            catch (Exception exception)
            {
                AppLog.Write("health", "Не удалось остановить VPN для проверки частного DNS", exception);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            try
            {
                VpnStarter.Start(context, config);
                AppLog.Write("health", "VPN перезапущен для повторной проверки частного DNS");
            }
            catch (Exception exception)
            {
                AppLog.Write("health",
                    "Не удалось перезапустить VPN в фоне — переподключите VPN вручную", exception);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("health", "Проверка частного DNS не удалась", exception);
        }
    }

    /// <summary>
    /// Строгий частный DNS включён, но подтверждённых адресов DoT нет. Возвращает null,
    /// если частный DNS выключен или в мягком режиме. Строка LinkProperties содержит
    /// <c>ValidatedPrivateDnsAddresses</c> начиная с Android 14; capability VALIDATED —
    /// вторая страховка, по ней сеть считается проверенной и при нестандартном формате.
    /// </summary>
    private static (bool Validated, string Name)? FindStrictPrivateDns()
    {
        try
        {
            if (Android.App.Application.Context.GetSystemService(Context.ConnectivityService)
                is not ConnectivityManager manager)
            {
                return null;
            }

            foreach (var network in manager.GetAllNetworks())
            {
                NetworkCapabilities? capabilities;
                try
                {
                    capabilities = manager.GetNetworkCapabilities(network);
                }
                catch (Exception)
                {
                    continue;
                }

                if (capabilities is null || !capabilities.HasTransport(TransportType.Vpn))
                {
                    continue;
                }

                LinkProperties? link;
                try
                {
                    link = manager.GetLinkProperties(network);
                }
                catch (Exception)
                {
                    continue;
                }

                if (link is null || !link.IsPrivateDnsActive)
                {
                    continue;
                }

                var name = link.PrivateDnsServerName;
                if (string.IsNullOrEmpty(name))
                {
                    // Мягкий режим: DoT не обязателен, резолв идёт обычным DNS.
                    continue;
                }

                var text = link.ToString() ?? string.Empty;
                const string marker = "ValidatedPrivateDnsAddresses: [";
                var index = text.IndexOf(marker, StringComparison.Ordinal);
                var validatedByAddresses = index >= 0 &&
                                           text.IndexOf(']', index) > index + marker.Length;
                var validated = validatedByAddresses ||
                                capabilities.HasCapability(NetCapability.Validated);
                return (validated, name);
            }
        }
        catch (Exception)
        {
            // Нет разрешения/сеть исчезла — просто не трогаем туннель.
        }

        return null;
    }

    private static int _healthBusy;

    private async Task HealthTickAsync()
    {
        if (Interlocked.CompareExchange(ref _healthBusy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (State != TunnelState.Running || IsBypassMode || !CoreRuntime.IsRunning)
            {
                return;
            }

            NodeStore.EnsureLoaded();
            var settings = NodeStore.Settings;
            if (!settings.AutoFailover || settings.AutomationPaused)
            {
                return;
            }

            // Нет ни Wi-Fi, ни мобильной сети — это не смерть сервера. Без проверки
            // устройство в фоне (Doze, экран выключен) переключалось бы зря.
            if (!HasUnderlyingNetwork())
            {
                return;
            }

            var selected = NodeStore.Selected;
            if (selected is null)
            {
                AppLog.Write("health", "Автопереключение: выбранный сервер неизвестен, беру загруженный");
                if (await SwitchToLoadedNodeAsync(null).ConfigureAwait(false) is null)
                {
                    AppLog.Write("health", "Автопереключение: загруженных рабочих серверов нет");
                }

                return;
            }

            var loaded = CoreRuntime.LoadedTags;
            if (!loaded.Contains(selected.Tag))
            {
                // Фоновая проверка могла выбрать сервер, которого нет в текущем конфиге.
                // Проверять его тег бессмысленно — сразу переключаемся на загруженный.
                var switched = await SwitchToLoadedNodeAsync(selected).ConfigureAwait(false);
                AppLog.Write("health", switched is null
                    ? "Автопереключение: выбранного сервера нет в конфиге, запасных нет"
                    : "Автопереключение: выбранный сервер вне конфига, выбран загруженный");
                if (switched is not null)
                {
                    _healthFailures = 0;
                }

                return;
            }

            int? delay;
            using (var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret))
            {
                var route = SettingsStore.Current;
                var probeUrl = "https://cp.cloudflare.com/generate_204";
                if (route.SiteRouteMode == SiteRouteMode.OnlyListed)
                {
                    var site = route.SiteList
                        .Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries |
                                                                  StringSplitOptions.TrimEntries)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(site))
                    {
                        probeUrl = site.Contains("://", StringComparison.Ordinal)
                            ? site
                            : "https://" + site.Trim('/') + "/";
                    }
                }

                delay = await api.TestDelayAsync(selected.Tag, 5000, CancellationToken.None,
                    probeUrl).ConfigureAwait(false);
            }

            // Пока шёл запрос, пользователь мог нажать «Дальше» — сбой старого сервера
            // не должен уменьшать счётчики или переключать только что выбранный.
            if (NodeStore.Selected?.Id != selected.Id)
            {
                return;
            }

            // Счётчик провалов принадлежит конкретному серверу: после переключения
            // на свежий он не должен сразу получить «2/2» от провалов старого.
            if (!string.Equals(_healthNodeId, NodeStore.Selected?.Id, StringComparison.OrdinalIgnoreCase))
            {
                _healthNodeId = NodeStore.Selected?.Id;
                _healthFailures = 0;
            }

            if (delay is not null)
            {
                _healthFailures = 0;
                return;
            }

            _healthFailures++;
            AppLog.Write("health", $"Сервер не ответил ({_healthFailures}/2)");
            if (_healthFailures < 2)
            {
                return;
            }

            _healthFailures = 0;

            // Два провала подряд — сервер не открывает выбранный сайт, переключаемся.
            // Статус и FailCount не меняем: проверка через активный туннель зависит
            // от маршрута (например, «только сайты из списка») и не доказывает смерть.

            var family = NodeRanking.Family(selected);
            // Выбранный сервис важнее скорости: сначала живой сервер, открывающий его,
            // затем следующий лучший из других. Свежесть проверки обязательна.
            var replacement = NodeFailover.Rank(
                    NodeStore.Snapshot().Where(node =>
                        node.Id != selected.Id && loaded.Contains(node.Tag)),
                    NodeStore.ServiceKey(ActiveService),
                    DateTime.UtcNow,
                    NodeStore.Settings.ConnectOnlyMine,
                    family)
                .FirstOrDefault();
            if (replacement is null)
            {
                // Запасного сервера в текущем конфиге нет: пересобираем конфиг по тем же
                // правилам, что и при обычном подключении, сохраняя выбранный сервис.
                var rebuilt = NodeStore.BuildTunnelConfig(
                    out var error, preferWorking: true, preferService: ActiveService);
                if (rebuilt is null)
                {
                    AppLog.Write("health", "Автопереключение: " + error);
                    return;
                }

                StartCore(rebuilt);
                UpdateNodeLabel(NodeStore.Selected?.DisplayDelay);
                AppLog.Write("health", "Автопереключение: конфиг пересобран");
                return;
            }

            using var switchApi = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
            await switchApi.SelectAsync(SingBoxConfigBuilder.SelectorTag, replacement.Tag, CancellationToken.None)
                .ConfigureAwait(false);
            NodeStore.Select(replacement);
            UpdateNodeLabel(replacement.DisplayDelay);
            AppLog.Write("health",
                $"Автопереключение: {NodeRanking.Family(selected)} → {NodeRanking.Family(replacement)}");
        }
        catch (Exception exception)
        {
            AppLog.Write("health", "Проверка живости сервера не удалась", exception);
        }
        finally
        {
            Volatile.Write(ref _healthBusy, 0);
        }
    }

    /// <summary>
    /// Проверка нашла сервер выбранного сервиса, а туннель работает через запасной
    /// из другого сервиса. Возвращаемся на выбранный сервис: тёплым переключением,
    /// если сервер уже в конфиге, иначе пересборкой конфига (короткий перезапуск ядра).
    /// При ошибке остаёмся на прежнем рабочем сервере.
    /// </summary>
    internal static void MaybeReturnToActiveService()
    {
        if (ActiveService is not { } service ||
            State != TunnelState.Running || IsBypassMode || !CoreRuntime.IsRunning)
        {
            return;
        }

        if (Interlocked.Exchange(ref _upgradeBusy, 1) == 1)
        {
            return;
        }

        try
        {
            var settings = NodeStore.Settings;
            if (!settings.AutoFailover || settings.AutomationPaused)
            {
                return;
            }

            var selected = NodeStore.Selected;
            if (selected is not null && NodeStore.OpenedFor(selected, service))
            {
                return;
            }

            // Не чаще раза в минуту: проверка обновляет десятки серверов подряд.
            var now = DateTime.UtcNow;
            if (now - _lastUpgradeUtc < TimeSpan.FromMinutes(1))
            {
                return;
            }

            var key = NodeStore.ServiceKey(service);
            var loaded = CoreRuntime.LoadedTags;
            var loadedPreferred = NodeFailover.Rank(
                    NodeStore.Snapshot().Where(node => loaded.Contains(node.Tag)),
                    key,
                    now,
                    settings.ConnectOnlyMine,
                    filter: node => (selected is null || node.Id != selected.Id) &&
                                    (!_upgradeRetryAfter.TryGetValue(node.Id, out var loadedRetry) ||
                                     loadedRetry <= now))
                .FirstOrDefault(node => NodeStore.OpenedFor(node, service));
            if (loadedPreferred is not null)
            {
                _lastUpgradeUtc = now;
                AppLog.Write("health",
                    $"«{service}» снова доступен: «{loadedPreferred.Name}» — тёплое переключение");
                _ = SwitchWarmAsync(loadedPreferred);
                return;
            }

            // В конфиге нужного сервиса нет: пересобираем с приоритетом сервиса.
            var preferred = NodeFailover.Rank(
                    NodeStore.Snapshot(),
                    key,
                    now,
                    settings.ConnectOnlyMine,
                    filter: node => (selected is null || node.Id != selected.Id) &&
                                    (!_upgradeRetryAfter.TryGetValue(node.Id, out var retryAt) ||
                                     retryAt <= now))
                .FirstOrDefault(node => NodeStore.OpenedFor(node, service));
            var active = Active;
            if (preferred is null || active is null)
            {
                return;
            }

            _lastUpgradeUtc = now;
            NodeStore.Select(preferred);
            var config = NodeStore.BuildTunnelConfig(
                out var error, preferWorking: true, preferService: service);
            if (config is null)
            {
                _upgradeRetryAfter[preferred.Id] = now.AddMinutes(15);
                if (selected is not null)
                {
                    NodeStore.Select(selected);
                }

                AppLog.Write("health", "Возврат на выбранный сервис: " + error);
                return;
            }

            AppLog.Write("health",
                $"«{service}» снова доступен: «{preferred.Name}» — конфиг пересобран");
            active.StartCore(config);
            if (State != TunnelState.Running)
            {
                _upgradeRetryAfter[preferred.Id] = DateTime.UtcNow.AddMinutes(15);
            }

            UpdateNodeLabel(NodeStore.Selected?.DisplayDelay);
        }
        catch (Exception exception)
        {
            AppLog.Write("health", "Возврат на выбранный сервис не удался", exception);
        }
        finally
        {
            Volatile.Write(ref _upgradeBusy, 0);
        }
    }

    private static async Task SwitchWarmAsync(ProxyNode node)
    {
        try
        {
            using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
            await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, node.Tag, CancellationToken.None)
                .ConfigureAwait(false);
            NodeStore.Select(node);
            UpdateNodeLabel(node.DisplayDelay);
        }
        catch (Exception exception)
        {
            _upgradeRetryAfter[node.Id] = DateTime.UtcNow.AddMinutes(15);
            AppLog.Write("health", "Тёплое переключение на выбранный сервис не удалось", exception);
        }
    }

    /// <summary>
    /// Переключает selector на рабочий сервер, который уже загружен в текущий конфиг.
    /// null — подходящего нет. Нужен, когда выбранный сервер выпал из конфига
    /// (например, его выбрала фоновая проверка) — иначе health-check тестирует
    /// несуществующий тег и «не переключается».
    /// </summary>
    private static async Task<ProxyNode?> SwitchToLoadedNodeAsync(ProxyNode? avoid)
    {
        var loaded = CoreRuntime.LoadedTags;
        var candidate = NodeFailover.Rank(
                NodeStore.Snapshot().Where(node => loaded.Contains(node.Tag)),
                NodeStore.ServiceKey(ActiveService),
                DateTime.UtcNow,
                NodeStore.Settings.ConnectOnlyMine,
                filter: node => avoid is null || node.Id != avoid.Id)
            .FirstOrDefault();
        if (candidate is null)
        {
            return null;
        }

        try
        {
            using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
            await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, candidate.Tag, CancellationToken.None)
                .ConfigureAwait(false);
            NodeStore.Select(candidate);
            UpdateNodeLabel(candidate.DisplayDelay);
            return candidate;
        }
        catch (Exception exception)
        {
            AppLog.Write("health", "Не удалось переключиться на загруженный сервер", exception);
            return null;
        }
    }

    /// <summary>Есть ли у устройства настоящая сеть (Wi-Fi/мобильная), а не только наш VPN.</summary>
    private bool HasUnderlyingNetwork()
    {
        try
        {
            var manager = _connectivity ??= (ConnectivityManager?)GetSystemService(ConnectivityService);
            if (manager is null)
            {
                return true;
            }

            foreach (var network in manager.GetAllNetworks())
            {
                var capabilities = manager.GetNetworkCapabilities(network);
                if (capabilities is null)
                {
                    continue;
                }

                if (capabilities.HasTransport(TransportType.Wifi) ||
                    capabilities.HasTransport(TransportType.Cellular) ||
                    capabilities.HasTransport(TransportType.Ethernet))
                {
                    return true;
                }
            }
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("VieNLink", "проверка сети: " + exception.Message);
        }

        return true;
    }

    /// <summary>Запуск ядра с конфигом sing-box. TUN ядро попросит само через <see cref="OpenTun"/>.</summary>
    private void StartCore(string configJson)
    {
        try
        {
            _lastConfigJson = configJson;

            // TUN-обход и прокси-сервер взаимоисключающие: при обычном подключении форвардер
            // должен остановиться, иначе его сокет остаётся занятым до следующего отключения.
            if (!_bypassMode)
            {
                _ = ForwarderHost.StopAsync();
            }

            // Пока ядро поднимает TUN, состояние «Подключаюсь…», а не «VPN не подключён».
            LastError = null;
            State = TunnelState.Starting;
            RefreshNotification();
            TunnelWidget.Update(this);

            CoreRuntime.EnsureSetup(this);

#if JAVA_PLATFORM
            // Java-платформа берёт TUN и protect у текущей службы.
            JavaBridge.SetService(this);
#endif

            // Конфиг сохраняем рядом с данными: по нему видно, что реально ушло в ядро.
            try
            {
                File.WriteAllText(Path.Combine(FilesDir!.AbsolutePath, "last-config.json"), configJson);
            }
            catch (Exception dumpError)
            {
                Android.Util.Log.Warn("VieNLink", "не удалось сохранить конфиг: " + dumpError.Message);
            }

            Android.Util.Log.Info("VieNLink", "шаг 1: version=" + Libbox.Libbox.Version());

            Libbox.Libbox.CheckConfig(configJson);
            Android.Util.Log.Info("VieNLink", "шаг 2: конфиг проверен");

            if (!CoreRuntime.Start(configJson))
            {
                LastError = "Ядро sing-box не запустилось — проверьте журнал";
                State = TunnelState.Stopped;
                TunnelWidget.Update(this);
                RefreshNotification();
                return;
            }

            Android.Util.Log.Info("VieNLink", "шаг 4: служба запущена");

#if JAVA_PLATFORM
            State = JavaBridge.HasTun() ? TunnelState.Running : TunnelState.Stopped;
#else
            State = _tunDescriptor is null ? TunnelState.Stopped : TunnelState.Running;
#endif

            // В уведомлении показываем сервер, чтобы его не выбирать вслепую.
            if (State == TunnelState.Running)
            {
                var selected = NodeStore.Selected;
                _nodeLabel = selected is null ? null : selected.DisplayDelay;
                RefreshNotification();
                StartHealthMonitor();
                StartPrivateDnsMonitor();
                TunnelWidget.Update(this);
            }

            Android.Util.Log.Info("VieNLink", "Ядро запущено, состояние: " + State);
        }
        catch (Exception exception)
        {
            LastError = string.IsNullOrWhiteSpace(exception.Message)
                ? "Не удалось запустить ядро"
                : "Не удалось запустить ядро: " + exception.Message;
            State = TunnelState.Stopped;
            VieNLink.AppLog.Write("android", "Не удалось запустить ядро", exception);
            Android.Util.Log.Error("VieNLink", "Ошибка запуска ядра: " + exception);

            // Ядро могло успеть создать TUN до ошибки: закрываем fd, иначе система
            // считает VPN поднятым, а трафик никуда не идёт.
#if JAVA_PLATFORM
            JavaBridge.CloseTun();
#else
            try
            {
                _tunDescriptor?.Close();
            }
            catch (Exception)
            {
                // fd мог быть уже закрыт ядром.
            }

            _tunDescriptor = null;
#endif
            CoreRuntime.Stop();

            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    StopForeground(StopForegroundFlags.Remove);
                }
            }
            catch (Exception)
            {
                // Уведомление могло быть уже снято.
            }

            StopSelf();
        }
    }

    /// <summary>Вызывается ядром: собрать TUN по его параметрам и вернуть fd.</summary>
    public int OpenTun(Libbox.ITunOptions? options)
    {
        Android.Util.Log.Info("VieNLink", "ядро просит TUN: " + (options is null ? "null" : "mtu=" + options.MTU));

        if (options is null)
        {
            return -1;
        }

        try
        {
            var builder = new Builder(this)
                .SetSession(SessionName)
                .SetMtu(options.MTU > 0 ? options.MTU : 1500);

            // Маршрутизация приложений: VpnService умеет либо «только список»
            // (AddAllowedApplication), либо «все, кроме списка» (AddDisallowedApplication).
            // Оба способа одновременно вызывать нельзя.
            var routing = _bypassMode ? null : AndroidSettingsStore.Current;
            // Маршрутизация по приложениям пока отключена: старый split-tunnel оставлял
            // YouTube и браузер вне туннеля, и они «не работали при рабочем сервере».
            // Весь трафик идёт через VPN (свой процесс исключается ниже).
            var mode = AppRouteMode.All;
            var selected = new List<string>();
            var applied = 0;

            if (mode == AppRouteMode.OnlyListed && selected.Count > 0)
            {
                foreach (var package in selected)
                {
                    // Свой процесс в туннель не пускаем: ядро ушло бы в собственный VPN.
                    if (package.Equals(PackageName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        builder.AddAllowedApplication(package);
                        applied++;
                    }
                    catch (Java.Lang.Exception exception)
                    {
                        // Приложение удалили после настройки — просто пропускаем.
                        Android.Util.Log.Warn("VieNLink", "apps: " + package + ": " + exception.Message);
                    }
                }
            }
            else if (mode == AppRouteMode.AllExceptListed && selected.Count > 0)
            {
                foreach (var package in selected)
                {
                    if (package.Equals(PackageName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        builder.AddDisallowedApplication(package);
                        applied++;
                    }
                    catch (Java.Lang.Exception exception)
                    {
                        Android.Util.Log.Warn("VieNLink", "apps: " + package + ": " + exception.Message);
                    }
                }
            }

            if (applied > 0)
            {
                Android.Util.Log.Info("VieNLink", $"apps: режим {mode}, приложений {applied}");
                if (mode == AppRouteMode.AllExceptListed)
                {
                    // Свой процесс вне туннеля: иначе запросы ядра уйдут в него же.
                    builder.AddDisallowedApplication(PackageName!);
                }
            }
            else
            {
                // «Все приложения» (или список пуст) — туннелируем всех, кроме себя.
                builder.AddDisallowedApplication(PackageName!);
            }

            AddPrefixes(builder, options.Inet4Address, asRoute: false);
            AddPrefixes(builder, options.Inet6Address, asRoute: false);

            var routes = AddPrefixes(builder, options.Inet4RouteAddress, asRoute: true);

            // Ядро может не передать маршруты вовсе (пустой список) — тогда в туннель
            // не попадал бы ни один пакет, и приложения ходили бы напрямую.
            if (routes == 0)
            {
                builder.AddRoute("0.0.0.0", 0);
            }

            AddDnsServers(builder, options.DNSServerAddress);

            // Не ставим SetBlocking(false): gvisor-стек ядра ожидает обычный блокирующий fd.
            // При перезагрузке конфига ядро просит TUN заново — старый fd закрываем.
            try
            {
                _tunDescriptor?.Close();
            }
            catch (Exception)
            {
                // Старый fd мог быть уже закрыт ядром.
            }

            _tunDescriptor = builder.Establish();
            if (_tunDescriptor is null)
            {
                Android.Util.Log.Warn("VieNLink", "TUN не создан: разрешение VPN не выдано");
                return -1;
            }

            State = TunnelState.Running;
            Android.Util.Log.Info("VieNLink", "TUN поднят, fd=" + _tunDescriptor.Fd);
            return _tunDescriptor.Fd;
        }
        catch (Exception exception)
        {
            LastError = "Не удалось поднять TUN: " + exception.Message;
            State = TunnelState.Stopped;
            VieNLink.AppLog.Write("android", "Не удалось поднять TUN", exception);
            Android.Util.Log.Error("VieNLink", "Ошибка TUN: " + exception);
            return -1;
        }
    }

    /// <summary>Прячет сокет от собственного туннеля (аналог protect() для нативных сокетов ядра).</summary>
    public void ProtectSocket(int fd)
    {
        Android.Util.Log.Debug("VieNLink", "protect(" + fd + ")");
        try
        {
            Protect(fd);
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("VieNLink", "protect(" + fd + ") не сработал: " + exception.Message);
        }
    }

    /// <summary>DNS-серверы текущей сети (не нашего VPN): их и используем как источник ответов.</summary>
    public List<string> GetUpstreamDnsServers()
    {
        var result = new List<string>();

        try
        {
            if (_connectivity is not null)
            {
                foreach (var network in _connectivity.GetAllNetworks())
                {
                    var capabilities = _connectivity.GetNetworkCapabilities(network);
                    if (capabilities is null ||
                        !capabilities.HasCapability(NetCapability.Internet) ||
                        capabilities.HasTransport(TransportType.Vpn))
                    {
                        continue;
                    }

                    var link = _connectivity.GetLinkProperties(network);
                    if (link?.DnsServers is null)
                    {
                        continue;
                    }

                    foreach (var address in link.DnsServers)
                    {
                        var host = address.HostAddress;
                        if (!string.IsNullOrWhiteSpace(host) && host.Contains('.'))
                        {
                            result.Add(host);
                        }
                    }

                    if (result.Count > 0)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("VieNLink", "не удалось получить DNS сети: " + exception.Message);
        }

        if (result.Count == 0)
        {
            result.Add("1.1.1.1");
        }

        return result;
    }

    /// <summary>Ядро просит сообщать о сетевом интерфейсе по умолчанию.</summary>
    public void StartDefaultInterfaceMonitor(Libbox.IInterfaceUpdateListener? listener)
    {
        if (listener is null)
        {
            return;
        }

        _interfaceListener = listener;
        _connectivity ??= (ConnectivityManager?)GetSystemService(ConnectivityService);

        if (_networkCallback is null && _connectivity is not null)
        {
            _networkCallback = new DefaultNetworkCallback(() => RefreshDefaultInterface());
            _connectivity.RegisterDefaultNetworkCallback(_networkCallback);
        }

        RefreshDefaultInterface();
    }

    public void CloseDefaultInterfaceMonitor(Libbox.IInterfaceUpdateListener? listener)
    {
        if (_networkCallback is not null && _connectivity is not null)
        {
            try
            {
                _connectivity.UnregisterNetworkCallback(_networkCallback);
            }
            catch (Exception)
            {
                // Колбэк мог быть уже снят системой.
            }
        }

        _networkCallback = null;
        _interfaceListener = null;
    }

    /// <summary>Находит физический интерфейс с интернетом (не наш VPN) и отдаёт его ядру.</summary>
    public void RefreshDefaultInterface()
    {
        if (_interfaceListener is null || _connectivity is null)
        {
            return;
        }

        try
        {
            foreach (var network in _connectivity.GetAllNetworks())
            {
                var capabilities = _connectivity.GetNetworkCapabilities(network);
                if (capabilities is null ||
                    !capabilities.HasCapability(NetCapability.Internet) ||
                    capabilities.HasTransport(TransportType.Vpn))
                {
                    continue;
                }

                var link = _connectivity.GetLinkProperties(network);
                var name = link?.InterfaceName;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var index = 0;
                try
                {
                    index = Java.Net.NetworkInterface.GetByName(name)?.Index ?? 0;
                }
                catch (Exception)
                {
                    // Индекс не критичен: ядро умеет работать и без него.
                }

                var metered = !capabilities.HasCapability(NetCapability.NotMetered);
                _interfaceListener.UpdateDefaultInterface(name, index, metered, false);
                Android.Util.Log.Info("VieNLink", $"интерфейс по умолчанию: {name} ({index}), metered={metered}");
                return;
            }
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("VieNLink", "не удалось определить интерфейс: " + exception.Message);
        }
    }

    private static int AddPrefixes(Builder builder, Libbox.IRoutePrefixIterator? iterator, bool asRoute)
    {
        if (iterator is null)
        {
            return 0;
        }

        var added = 0;
        while (iterator.HasNext)
        {
            var prefix = iterator.Next();
            var address = prefix?.Address();
            if (string.IsNullOrWhiteSpace(address) || prefix is null)
            {
                continue;
            }

            var length = prefix.Prefix();
            if (asRoute)
            {
                builder.AddRoute(address, length);
            }
            else
            {
                builder.AddAddress(address, length);
            }

            added++;
        }

        return added;
    }

    private static void AddDnsServers(Builder builder, Libbox.IStringIterator? iterator)
    {
        var added = false;
        if (iterator is not null)
        {
            while (iterator.HasNext)
            {
                var server = iterator.Next();
                if (!string.IsNullOrWhiteSpace(server))
                {
                    builder.AddDnsServer(server);
                    added = true;
                }
            }
        }

        // Ядро может не передать DNS (в конфиге sing-box нет секции dns) — тогда
        // Android-приложения остаются без резолвера. Подставляем публичный.
        if (!added)
        {
            builder.AddDnsServer("1.1.1.1");
        }
    }

    /// <summary>
    /// После включения обхода проверяет, открывается ли страница YouTube через текущую
    /// стратегию. Если нет — сам перебирает стратегии (страница, затем видео), сохраняет
    /// результат в пресет текущей сети и перезапускает обход.
    /// </summary>
    private void ScheduleAutoTune()
    {
        _autoTuneCts?.Cancel();
        _autoTuneCts?.Dispose();
        _autoTuneCts = new CancellationTokenSource();
        _ = AutoTuneIfNeededAsync(_autoTuneCts.Token);
    }

    private async Task AutoTuneIfNeededAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (State != TunnelState.Running || !_bypassMode)
            {
                return;
            }

            if (DateTime.UtcNow - _lastAutoTuneUtc < TimeSpan.FromMinutes(15))
            {
                return;
            }

            var port = ForwarderHost.Port;
            if (port <= 0)
            {
                return;
            }

            var probe = await StrategyAutotune.CheckPageAsync(port, cancellationToken)
                .ConfigureAwait(false);
            if (probe.Strong)
            {
                AppLog.Write("tun-bypass",
                    $"Автоподбор: страница и превью открылись ({probe.Score}/3), " +
                    "стратегия рабочая");
                AndroidSettingsStore.RecordAutotune(
                    $"Фоновая проверка: страница и превью открылись ({probe.Score}/3), " +
                    $"стратегия {ForwarderHost.Mode} рабочая", null);
                // Подтверждаем параметры этой сети: они попадут в базу по операторам.
                NetworkPresets.RememberSuccess(
                    this,
                    ForwarderHost.Mode.ToString(),
                    NetworkPresets.GetVideo(this),
                    NetworkPresets.GetBlockQuic(this));
                return;
            }

            _lastAutoTuneUtc = DateTime.UtcNow;
            AppLog.Write("tun-bypass",
                $"Автоподбор: через обход пришло {probe.Score}/3 ({probe.Describe}), " +
                $"ищу первую рабочую; " + NetworkPresets.Describe(this));
            _nodeLabel = "Автоподбор стратегии…";
            RefreshNotification();

            IProgress<AutotuneStep> progress = new Progress<AutotuneStep>(
                step => AppLog.Write("tun-bypass", "автоподбор: " + step.Text));

            var preferredPage = Enum.TryParse<StreamFragmentMode>(
                NetworkPresets.GetPage(this), out var pageMode)
                ? pageMode
                : (StreamFragmentMode?)null;
            var preferredVideo = Enum.TryParse<StreamFragmentMode>(
                NetworkPresets.GetVideo(this), out var videoMode)
                ? videoMode
                : preferredPage;
            var page = await StrategyAutotune.RunAsync(
                AutotuneScope.Page, progress, cancellationToken, preferredPage).ConfigureAwait(false);
            if (page.Aborted)
            {
                AppLog.Write("tun-bypass", "Автоподбор прерван: сеть перестала отвечать");
                return;
            }

            var video = await StrategyAutotune.RunAsync(
                AutotuneScope.Video, progress, cancellationToken, preferredVideo).ConfigureAwait(false);

            // QUIC проверяем один раз на подбор: от TCP-стратегии он не зависит.
            var quicOk = await StrategyAutotune.ProbeQuicAsync(port, cancellationToken).ConfigureAwait(false);
            var quicChanged = false;
            if (quicOk)
            {
                if (NetworkPresets.GetBlockQuic(this))
                {
                    NetworkPresets.SetBlockQuic(this, false);
                    quicChanged = true;
                }

                AppLog.Write("tun-bypass", "Автоподбор: QUIC-проба прошла — оставляю через релей");
            }
            else
            {
                // По синтетической пробе не блокируем: решение примет реальный QUIC-трафик.
                AppLog.Write("tun-bypass",
                    "Автоподбор: QUIC-проба не прошла — настройку не меняю (решит реальный трафик)");
            }

            var pageStrategy = page.Strategy;
            var quicProbeText = quicOk
                ? "проходит (через релей)"
                : "проба не прошла — решит реальный трафик";
            if (pageStrategy is null)
            {
                AppLog.Write("tun-bypass", "Автоподбор: рабочая стратегия не найдена");
                AndroidSettingsStore.RecordAutotune(
                    "Фоновый подбор: рабочая стратегия не найдена", quicProbeText);
                _nodeLabel = "TUN-обход (бета): нет рабочей стратегии";
                RefreshNotification();
                return;
            }

            // Видео-пресет меняем только по настоящим байтам потока. Иначе не найденная
            // видео-стратегия молча подменялась страничной — и «видео работает» было ложью.
            if (video.Strategy is not null)
            {
                NetworkPresets.SetVideo(this, video.Strategy.Mode.ToString());
            }

            NetworkPresets.SetPage(this, pageStrategy.Mode.ToString());

            // Итог фонового подбора запоминаем: экран TUN покажет его и после перезапуска.
            AndroidSettingsStore.RecordAutotune(
                $"Фоновый подбор: {pageStrategy} — {page.Score} из {page.Total}" +
                (page.Strong ? " (страница и превью)" : " (только страница)") +
                (video.Strategy is null
                    ? ", видео не проверено"
                    : video.AudioOnly
                        ? $", только звук {video.Strategy}"
                        : $", видео {video.Strategy}"),
                quicProbeText);

            if (page.Strong)
            {
                NetworkPresets.RememberSuccess(
                    this,
                    pageStrategy.Mode.ToString(),
                    video.Strategy?.Mode.ToString() ?? NetworkPresets.GetVideo(this),
                    NetworkPresets.GetBlockQuic(this),
                    transport: video.Strategy is not null && !video.SniOnly && !video.AudioOnly
                        ? "tcp"
                        : string.Empty,
                    videoKbps: video.Strategy is not null && !video.SniOnly && !video.AudioOnly
                        ? video.SpeedKbps
                        : 0);
            }

            AppLog.Write("tun-bypass",
                $"Автоподбор: страница {pageStrategy.Name} ({page.Score}/{page.Total}, " +
                $"{(page.Strong ? "страница и превью" : "только страница")}" +
                $"{(page.SpeedKbps > 0 ? $", {page.SpeedKbps} КБ/с" : string.Empty)}), " +
                $"видео {(video.Strategy is null ? "не найдено" : video.AudioOnly
                    ? $"не идёт, звук {video.Strategy.Name} ({video.SpeedKbps} КБ/с)"
                    : video.SniOnly
                        ? $"медиасервер отвечает ({video.Strategy.Name}), поток не проверялся"
                        : $"{video.Strategy.Name} ({video.Score}/{video.Total}" +
                          (video.SpeedKbps > 0 ? $", {video.SpeedKbps} КБ/с" : string.Empty) + ")")}");
            _nodeLabel = "Автоподбор: " + pageStrategy.Name;
            RefreshNotification();

            // Не применяем заведомо слабый результат: если стратегия не лучше текущей и не
            // подтвердила превью, перезапуск только собьёт туннель и зациклит health-check.
            if (!page.Strong && page.Score <= probe.Score && !quicChanged)
            {
                AppLog.Write("tun-bypass", "Автоподбор: лучше текущей нет, перезапуск не нужен");
                return;
            }

            if (pageStrategy.Mode == ForwarderHost.Mode && !quicChanged)
            {
                AppLog.Write("tun-bypass", "Автоподбор: стратегия не изменилась, перезапуск не нужен");
                return;
            }

            // Применяем подобранное: перезапускаем обход с новой стратегией.
            StopTunnel();
            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            StartBypass(pageStrategy.Mode);
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Write("tun-bypass", "Автоподбор не удался", exception);
        }
    }

    private void StopTunnel()
    {
        _autoTuneCts?.Cancel();
        _autoTuneCts?.Dispose();
        _autoTuneCts = null;
        _bypassMode = false; IsBypassMode = false;
        ActiveService = null;
        StopHealthMonitor();
        StopPrivateDnsMonitor();

        // Перезапуск по нашей инициативе не должен сбрасывать счётчик попыток:
        // иначе сломанный частный DNS вызывал бы бесконечные переподключения.
        if (!_privateDnsSelfRestart)
        {
            _privateDnsRestarts = 0;
            _lastPrivateDnsRestartUtc = DateTime.MinValue;
            _lastPrivateDnsHintUtc = DateTime.MinValue;
        }

        _privateDnsSelfRestart = false;

        // Ядро закрываем через CoreRuntime: у процесса один CommandServer.
        CoreRuntime.Stop();

        // Форвардер TUN-обхода останавливаем в фоне: OnDestroy не должен ждать сокеты.
        _ = ForwarderHost.StopAsync();

        try
        {
            _tunDescriptor?.Close();
        }
        catch (Exception)
        {
            // fd мог быть уже закрыт ядром.
        }

        _tunDescriptor = null;
        State = TunnelState.Stopped;
        LastError = null;
        _nodeLabel = null;
        TunnelWidget.Update(this);

        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                StopForeground(StopForegroundFlags.Remove);
            }
        }
        catch (Exception)
        {
            // Уведомление могло быть уже снято системой.
        }

        Android.Util.Log.Info("VieNLink", "Туннель остановлен");
    }
}

