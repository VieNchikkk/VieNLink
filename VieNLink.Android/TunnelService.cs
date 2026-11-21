using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;

namespace VieNLink.Mobile;

public enum TunnelState
{
    Stopped = 0,
    Running = 1,
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

    /// <summary>TUN-обход без узла: TUN → встроенный форвардер с фрагментацией TLS.</summary>
    public const string ActionBypass = "com.vienlink.app.BYPASS";

    /// <summary>Стратегия фрагментации TUN-обхода (имя <see cref="StreamFragmentMode"/>).</summary>
    public const string ExtraStrategy = "strategy";

    /// <summary>Готовый конфиг sing-box из <c>SingBoxConfigBuilder</c>; если нет — прямой выход.</summary>
    public const string ExtraConfig = "config";

    private const string SessionName = "VieNLink";
    private const string DefaultTunAddress = "172.19.0.1";

    /// <summary>
    /// Временный конфиг для проверки связки: TUN + прямой выход без узла.
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

    public static TunnelState State { get; private set; } = TunnelState.Stopped;

    /// <summary>
    /// Текущая служба для платформы libbox: из неё берутся TUN и protect.
    /// null — ядро работает в режиме проверки узлов, TUN не нужен.
    /// </summary>
    public static TunnelService? Active { get; private set; }

    private ParcelFileDescriptor? _tunDescriptor;
    private ConnectivityManager? _connectivity;
    private Libbox.IInterfaceUpdateListener? _interfaceListener;
    private DefaultNetworkCallback? _networkCallback;
    private bool _bypassMode;

    /// <summary>Служба поднимает TUN-обход: проверка узлов в отдельном процессе недоступна.</summary>
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

        if (intent?.Action == ActionBypass)
        {
            // Передаём имя режима, а не индекс: порядок списка в интерфейсе и в Core
            // может отличаться (RecordSplit показывается первым).
            var requested = intent.GetStringExtra(ExtraStrategy);
            var mode = Enum.TryParse<StreamFragmentMode>(requested, out var parsed)
                ? parsed
                : StreamFragmentMode.RecordSplit;
            StartBypass(mode);
            return StartCommandResult.Sticky;
        }

        _bypassMode = false; IsBypassMode = false;

        if (intent?.Action == ActionAuto)
        {
            // После загрузки человека нет: нельзя поднимать туннель на узле, который
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
    /// TUN-обход без прокси-узла: TUN → встроенный SOCKS5-форвардер, который режет
    /// TLS ClientHello. Сокеты форвардера идут мимо VPN, потому что процесс исключён
    /// из туннеля на уровне <c>VpnService</c>.
    /// </summary>
    private void StartBypass(StreamFragmentMode mode)
    {
        _bypassMode = true; IsBypassMode = true;
        var strategy = TlsFragmentForwarder.Strategies.FirstOrDefault(item => item.Mode == mode)
                       ?? TlsFragmentForwarder.Strategies[0];

        _ = Task.Run(async () =>
        {
            try
            {
                var port = await ForwarderHost.StartAsync(mode).ConfigureAwait(false);

                var settings = SettingsStore.Current;
                var config = SingBoxConfigBuilder.BuildTunBypass(
                    port,
                    CoreRuntime.ApiPort,
                    CoreRuntime.ApiSecret,
                    settings.TunBypassBlockQuic,
                    processName: null,
                    onlyProcess: null,
                    // На Android системный резолвер ядру недоступен. 1.1.1.1 у российских
                    // операторов часто заблокирован, поэтому по умолчанию Яндекс-DNS.
                    dnsServer: "77.88.8.8");

                StartCore(config);

                if (State == TunnelState.Running)
                {
                    _nodeLabel = "TUN-обход: " + strategy.Name;
                    RefreshNotification();
                }

                AppLog.Write("tun-bypass", $"TUN-обход запущен: {strategy.Name}, форвардер {port}");
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
    /// Уведомление: текущий узел, переход к списку узлов и отключение в один тап.
    /// Обновляется при смене узла (<see cref="UpdateNodeLabel"/>).
    /// </summary>
    private Notification BuildNotification()
    {
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        var text = State == TunnelState.Running
            ? _nodeLabel is null ? "Туннель работает" : "Подключено · " + _nodeLabel
            : "Подключение…";

        builder
            .SetContentTitle("VieNLink")
            .SetContentText(text)
            .SetSmallIcon(Android.Resource.Drawable.SymDefAppIcon)
            .SetOngoing(true)
            .SetShowWhen(false);

        var open = PendingIntent.GetActivity(
            this, 1, new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (open is not null)
        {
            builder.SetContentIntent(open);
        }

        var nodes = PendingIntent.GetActivity(
            this, 2, new Intent(this, typeof(NodesActivity)).AddFlags(ActivityFlags.NewTask),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (nodes is not null)
        {
            builder.AddAction(0, "Узлы", nodes);
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

    /// <summary>Показывает в уведомлении выбранный узел (вызывается при подключении и переключении).</summary>
    public static void UpdateNodeLabel(string? label)
    {
        _nodeLabel = label;
        Active?.RefreshNotification();
    }

    /// <summary>Запуск ядра с конфигом sing-box. TUN ядро попросит само через <see cref="OpenTun"/>.</summary>
    private void StartCore(string configJson)
    {
        try
        {
            CoreRuntime.EnsureSetup(this);

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
                return;
            }

            Android.Util.Log.Info("VieNLink", "шаг 4: служба запущена");

            State = _tunDescriptor is null ? TunnelState.Stopped : TunnelState.Running;

            // В уведомлении показываем узел, чтобы его не выбирать вслепую.
            if (State == TunnelState.Running)
            {
                var selected = NodeStore.Selected;
                _nodeLabel = selected is null ? null : selected.DisplayDelay;
                RefreshNotification();
            }

            Android.Util.Log.Info("VieNLink", "Ядро запущено, состояние: " + State);
        }
        catch (Exception exception)
        {
            State = TunnelState.Stopped;
            VieNLink.AppLog.Write("android", "Не удалось запустить ядро", exception);
            Android.Util.Log.Error("VieNLink", "Ошибка запуска ядра: " + exception);

            // Ядро могло успеть создать TUN до ошибки: закрываем fd, иначе система
            // считает VPN поднятым, а трафик никуда не идёт.
            try
            {
                _tunDescriptor?.Close();
            }
            catch (Exception)
            {
                // fd мог быть уже закрыт ядром.
            }

            _tunDescriptor = null;
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

            // Split-tunnel: если выбраны приложения, через VPN идут только они.
            // Список разрешённых и «не наш процесс» взаимоисключающие — Android не даёт
            // вызвать AddAllowedApplication и AddDisallowedApplication одновременно.
            var split = _bypassMode ? null : AndroidSettingsStore.Current;
            var allowed = 0;
            if (split is { SplitTunnel: true } && split.AllowedPackages.Count > 0)
            {
                foreach (var package in split.AllowedPackages.ToList())
                {
                    try
                    {
                        builder.AddAllowedApplication(package);
                        allowed++;
                    }
                    catch (Java.Lang.Exception exception)
                    {
                        // Приложение удалили после настройки — просто пропускаем.
                        Android.Util.Log.Warn("VieNLink", "split: " + package + ": " + exception.Message);
                    }
                }
            }

            if (allowed > 0)
            {
                Android.Util.Log.Info("VieNLink", "split-tunnel: приложений " + allowed);
            }
            else
            {
                // Свой процесс вне туннеля: иначе запросы ядра уйдут в него же.
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
            _networkCallback = new DefaultNetworkCallback(this);
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

    private void StopTunnel()
    {
        _bypassMode = false; IsBypassMode = false;

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
        _nodeLabel = null;

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

