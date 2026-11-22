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

    /// <summary>Выбрать лучший проверенный сервер и переключиться без пересоздания TUN.</summary>
    public const string ActionBest = "com.vienlink.app.BEST";

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

    public static TunnelState State { get; private set; } = TunnelState.Stopped;

    /// <summary>
    /// Текущая служба для платформы libbox: из неё берутся TUN и protect.
    /// null — ядро работает в режиме проверки серверов, TUN не нужен.
    /// </summary>
    public static TunnelService? Active { get; private set; }

    private ParcelFileDescriptor? _tunDescriptor;
    private ConnectivityManager? _connectivity;
    private Libbox.IInterfaceUpdateListener? _interfaceListener;
    private DefaultNetworkCallback? _networkCallback;
    private bool _bypassMode;
    private static Timer? _healthTimer;
    private static int _healthFailures;

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

        if (intent.Action == ActionBest)
        {
            _ = SwitchToBestAsync();
            return StartCommandResult.Sticky;
        }

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
                var mode = Enum.TryParse<StreamFragmentMode>(settings.TunBypassStrategy, out var parsed)
                    ? parsed
                    : StreamFragmentMode.RecordSplit;
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
        AndroidSettingsStore.Current.TunBypassStrategy = mode.ToString();
        AndroidSettingsStore.Save();
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
                    _nodeLabel = "TUN-обход (бета): " + strategy.Name;
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
    /// Уведомление: текущий сервер, переход к списку серверов и отключение в один тап.
    /// Обновляется при смене сервера (<see cref="UpdateNodeLabel"/>).
    /// </summary>
    private Notification BuildNotification()
    {
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        var text = State == TunnelState.Running
            ? _nodeLabel is null ? "Туннель работает" : "Подключено · " + _nodeLabel
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

        var best = PendingIntent.GetService(
            this, 4, new Intent(this, typeof(TunnelService)).SetAction(ActionBest),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (best is not null)
        {
            builder.AddAction(0, "Лучший", best);
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

    /// <summary>Действие «Лучший» в уведомлении: проверенный сервер с максимальным доступом.</summary>
    private async Task SwitchToBestAsync()
    {
        try
        {
            NodeStore.EnsureLoaded();
            var best = NodeStore.SelectBestWorking();
            if (best is null)
            {
                AppLog.Write("health", "Кнопка «Лучший»: живых проверенных серверов нет");
                return;
            }

            if (State == TunnelState.Running && CoreRuntime.IsRunning &&
                CoreRuntime.LoadedTags.Contains(best.Tag))
            {
                using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
                await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, best.Tag, CancellationToken.None)
                    .ConfigureAwait(false);
                UpdateNodeLabel(best.DisplayDelay);
                AppLog.Write("health", "Уведомление: выбран лучший сервер " + best.Name);
                return;
            }

            var config = NodeStore.BuildTunnelConfig(out var error, preferWorking: true);
            if (config is null)
            {
                AppLog.Write("health", "Кнопка «Лучший»: " + error);
                return;
            }

            StartCore(config);
            UpdateNodeLabel(NodeStore.Selected?.DisplayDelay);
            AppLog.Write("health", "Уведомление: туннель пересобран на лучший сервер");
        }
        catch (Exception exception)
        {
            AppLog.Write("health", "Кнопка «Лучший» не сработала", exception);
        }
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
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
    }

    private void StopHealthMonitor()
    {
        _healthTimer?.Dispose();
        _healthTimer = null;
        _healthFailures = 0;
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
                delay = await api.TestDelayAsync(selected.Tag, 5000, CancellationToken.None,
                    "https://cp.cloudflare.com/generate_204").ConfigureAwait(false);
            }

            // Пока шёл запрос, пользователь мог нажать «Дальше» — сбой старого сервера
            // не должен уменьшать счётчики или переключать только что выбранный.
            if (NodeStore.Selected?.Id != selected.Id)
            {
                return;
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

            // Два провала подряд — сервер действительно перестал открывать сайт.
            // Отмечаем это и в списке: счётчик сервисов сразу перестаёт его показывать.
            NodeHealth.RegisterFailure(selected);
            NodeStore.SaveNodes();

            var family = NodeRanking.Family(selected);
            var replacement = NodeStore.Snapshot()
                .Where(node => node.Id != selected.Id &&
                               node.Status == NodeStatus.Working &&
                               node.DelayMs is not null &&
                               loaded.Contains(node.Tag))
                .OrderBy(node => node.DelayMs)
                .ThenByDescending(node => NodeRanking.Family(node) != family)
                .ThenByDescending(node => node.OpenDomains)
                .FirstOrDefault();
            if (replacement is null)
            {
                // Запасного сервера в текущем конфиге нет: пересобираем конфиг по тем же
                // правилам, что и при обычном подключении.
                var rebuilt = NodeStore.BuildTunnelConfig(out var error, preferWorking: true);
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
    /// Переключает selector на рабочий сервер, который уже загружен в текущий конфиг.
    /// null — подходящего нет. Нужен, когда выбранный сервер выпал из конфига
    /// (например, его выбрала фоновая проверка) — иначе health-check тестирует
    /// несуществующий тег и «не переключается».
    /// </summary>
    private static async Task<ProxyNode?> SwitchToLoadedNodeAsync(ProxyNode? avoid)
    {
        var loaded = CoreRuntime.LoadedTags;
        var candidate = NodeStore.Snapshot()
            .Where(node => node.Status == NodeStatus.Working &&
                           node.DelayMs is not null &&
                           loaded.Contains(node.Tag) &&
                           (avoid is null || node.Id != avoid.Id))
            .OrderBy(node => node.DelayMs)
            .ThenByDescending(node => node.OpenDomains)
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
            // TUN-обход и прокси-сервер взаимоисключающие: при обычном подключении форвардер
            // должен остановиться, иначе его сокет остаётся занятым до следующего отключения.
            if (!_bypassMode)
            {
                _ = ForwarderHost.StopAsync();
            }

            // Пока ядро поднимает TUN, состояние «Подключаюсь…», а не «VPN не подключён».
            State = TunnelState.Starting;
            RefreshNotification();
            TunnelWidget.Update(this);

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
                State = TunnelState.Stopped;
                TunnelWidget.Update(this);
                return;
            }

            Android.Util.Log.Info("VieNLink", "шаг 4: служба запущена");

            State = _tunDescriptor is null ? TunnelState.Stopped : TunnelState.Running;

            // В уведомлении показываем сервер, чтобы его не выбирать вслепую.
            if (State == TunnelState.Running)
            {
                var selected = NodeStore.Selected;
                _nodeLabel = selected is null ? null : selected.DisplayDelay;
                RefreshNotification();
                StartHealthMonitor();
                TunnelWidget.Update(this);
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

            // Маршрутизация приложений: VpnService умеет либо «только список»
            // (AddAllowedApplication), либо «все, кроме списка» (AddDisallowedApplication).
            // Оба способа одновременно вызывать нельзя.
            var routing = _bypassMode ? null : AndroidSettingsStore.Current;
            var mode = routing?.AppRouteMode ?? AppRouteMode.All;
            var selected = routing?.AllowedPackages.ToList() ?? new List<string>();
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
        StopHealthMonitor();

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

