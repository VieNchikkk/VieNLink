using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Главный экран Android-версии: состояние туннеля, выбранный узел и переходы
/// к списку узлов и источников. Подключение идёт через общий <see cref="VpnStarter"/>.
/// </summary>
[Activity(Label = "VieNLink", MainLauncher = true, Theme = "@android:style/Theme.Material.Light.NoActionBar")]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "vienlink")]
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "text/plain")]
public sealed class MainActivity : Activity
{
    private const int VpnRequestCode = 1001;

    private TextView? _status;
    private TextView? _node;
    private EditText? _link;
    private string? _pendingConfig;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(
            UiMetrics.Dp(this, 20),
            UiMetrics.StatusBar(this) + UiMetrics.Dp(this, 12),
            UiMetrics.Dp(this, 20),
            UiMetrics.NavigationBar(this) + UiMetrics.Dp(this, 12));

        var title = new TextView(this) { Text = "VieNLink", TextSize = 28f };
        _status = new TextView(this) { TextSize = 15f };
        _node = new TextView(this) { TextSize = 15f };
        _link = new EditText(this) { Hint = "Необязательно: ссылка vless://… или подписка" };

        var nodes = new Button(this) { Text = "Узлы и источники" };
        var settings = new Button(this) { Text = "Настройки" };
        var diagnostics = new Button(this) { Text = "Диагностика" };
        var log = new Button(this) { Text = "Журнал" };
        var bypass = new Button(this) { Text = "TUN-обход (без узла)" };
        var connect = new Button(this) { Text = "Включить VPN" };
        var stop = new Button(this) { Text = "Выключить VPN" };

        // Быстрое подключение по сервису: узел уже проверен, пользователь ничего не настраивает.
        var quickYoutube = new Button(this) { Text = "YouTube" };
        var quickTelegram = new Button(this) { Text = "Telegram" };
        var quickDiscord = new Button(this) { Text = "Discord" };
        var quickUniversal = new Button(this) { Text = "Универсальный" };
        quickYoutube.Click += (_, _) => StartForService(NodeStore.QuickService.YouTube);
        quickTelegram.Click += (_, _) => StartForService(NodeStore.QuickService.Telegram);
        quickDiscord.Click += (_, _) => StartForService(NodeStore.QuickService.Discord);
        quickUniversal.Click += (_, _) => StartForService(NodeStore.QuickService.Universal);

        var quickTop = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        quickTop.AddView(quickYoutube, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        quickTop.AddView(quickTelegram, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var quickBottom = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        quickBottom.AddView(quickDiscord, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        quickBottom.AddView(quickUniversal, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        nodes.Click += (_, _) => StartActivity(typeof(NodesActivity));
        settings.Click += (_, _) => StartActivity(typeof(SettingsActivity));
        diagnostics.Click += (_, _) => StartActivity(typeof(DiagnosticsActivity));
        log.Click += (_, _) => StartActivity(typeof(LogActivity));
        bypass.Click += (_, _) => StartActivity(typeof(BypassActivity));
        connect.Click += (_, _) => StartVpn();
        stop.Click += (_, _) => StopVpn();

        var navigation = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        navigation.AddView(nodes, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        navigation.AddView(settings, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var tools = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        tools.AddView(diagnostics, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        tools.AddView(log, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        root.AddView(title);
        root.AddView(new TextView(this)
        {
            Text = "Ядро: " + (HasLibBox() ? "libbox найден" : "libbox.aar не подключён"),
            TextSize = 14f,
        });
        root.AddView(_status);
        root.AddView(_node);
        root.AddView(navigation);
        root.AddView(tools);
        root.AddView(bypass);
        root.AddView(new TextView(this) { Text = "Быстрое подключение по сервису", TextSize = 14f });
        root.AddView(quickTop);
        root.AddView(quickBottom);
        root.AddView(_link);
        root.AddView(connect);
        root.AddView(stop);

        SetContentView(root);

        EnsureNotificationPermission();

        // Совместимость со spike: ссылку можно передать для автоматической проверки.
        //   adb shell am start -n com.vienlink.app/... -e linkB64 "<base64>"
        var shared = Intent?.GetStringExtra("link");
        if (string.IsNullOrWhiteSpace(shared))
        {
            var encoded = Intent?.GetStringExtra("linkB64");
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                try
                {
                    shared = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                }
                catch (FormatException)
                {
                    SetStatus("Ссылка в base64 повреждена");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(shared) && _link is not null)
        {
            _link.Text = shared;
        }

        // Автопроверка через adb: главный экран остаётся экспортированным,
        // экраны узлов и источников наружу не открываются.
        if (Intent?.GetBooleanExtra("openNodes", false) == true)
        {
            OpenNodes();
        }

        HandleImport(Intent);
        HandleDebugCommands(Intent);
        RefreshStatus();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;

        // Повторный запуск через adb или по ссылке: экран уже открыт, разбираем новые extras.
        if (intent?.GetBooleanExtra("openNodes", false) == true)
        {
            OpenNodes();
        }

        HandleImport(intent);
        HandleDebugCommands(intent);
        RefreshStatus();
    }

    /// <summary>Отладочные команды для проверки без экрана: автозапуск и остановка.</summary>
    private void HandleDebugCommands(Intent? intent)
    {
        if (intent is null)
        {
            return;
        }

        if (intent.GetBooleanExtra("autoConnect", false))
        {
            AppLog.Write("android", "Отладка: автоподключение через ActionAuto");
            VpnStarter.StartAuto(this);
        }

        if (intent.GetBooleanExtra("stopTunnel", false))
        {
            VpnStarter.Stop(this);
        }
    }

    /// <summary>
    /// Импорт из «Поделиться» или deep link <c>vienlink://sub?url=…</c> /
    /// <c>vienlink://node?link=…</c>. Ссылка добавляется источником и сразу загружается.
    /// </summary>
    private async void HandleImport(Intent? intent)
    {
        var payload = ExtractImport(intent);
        if (payload is null)
        {
            return;
        }

        SetStatus("Импортирую ссылку…");

        try
        {
            var (message, added) = await Task.Run(() =>
            {
                NodeStore.EnsureLoaded();
                var before = NodeStore.SnapshotSources().Select(source => source.Id).ToHashSet();
                var result = NodeStore.AddSource(payload, null, mine: false);
                var created = NodeStore.SnapshotSources().FirstOrDefault(source => !before.Contains(source.Id));
                return (result, created);
            });

            SetStatus(message);
            if (added is null)
            {
                Toast.MakeText(this, message, ToastLength.Long)?.Show();
                return;
            }

            var progress = new Progress<string>(SetStatus);
            var loaded = await NodeStore.ImportSourceAsync(added, progress, CancellationToken.None);
            Toast.MakeText(this, loaded, ToastLength.Long)?.Show();
            StartActivity(typeof(SourcesActivity));
        }
        catch (Exception exception)
        {
            AppLog.Write("android", "Не удалось импортировать ссылку", exception);
            SetStatus("Импорт не удался: " + exception.Message);
        }
    }

    private static string? ExtractImport(Intent? intent)
    {
        if (intent is null)
        {
            return null;
        }

        if (intent.Action == Intent.ActionSend)
        {
            var shared = intent.GetStringExtra(Intent.ExtraText)?.Trim();
            return string.IsNullOrWhiteSpace(shared) ? null : shared;
        }

        if (intent.Action == Intent.ActionView && intent.Data is { } uri &&
            uri.Scheme?.Equals("vienlink", StringComparison.OrdinalIgnoreCase) == true)
        {
            var value = uri.GetQueryParameter("url") ?? uri.GetQueryParameter("link");
            if (string.IsNullOrWhiteSpace(value))
            {
                value = uri.LastPathSegment;
            }

            return string.IsNullOrWhiteSpace(value) ? null : Uri.UnescapeDataString(value).Trim();
        }

        return null;
    }

    /// <summary>Android 13+ не показывает foreground-уведомление без разрешения.</summary>
    private void EnsureNotificationPermission()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
        {
            return;
        }

        try
        {
            if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) !=
                Android.Content.PM.Permission.Granted)
            {
                RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 1002);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("android", "Не удалось запросить разрешение уведомлений", exception);
        }
    }

    private void OpenNodes()
    {
        var nodes = new Intent(this, typeof(NodesActivity));
        foreach (var key in new[] { "refresh", "test", "connect" })
        {
            if (Intent?.HasExtra(key) == true)
            {
                nodes.PutExtra(key, Intent.GetBooleanExtra(key, false));
            }
        }

        var selectTag = Intent?.GetStringExtra("selectTag");
        if (!string.IsNullOrWhiteSpace(selectTag))
        {
            nodes.PutExtra("selectTag", selectTag);
        }

        StartActivity(nodes);
    }

    protected override void OnResume()
    {
        base.OnResume();
        RefreshStatus();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != VpnRequestCode)
        {
            return;
        }

        if (resultCode == Result.Ok)
        {
            VpnStarter.Start(this, _pendingConfig ?? string.Empty);
            SetStatus("Запускаю туннель…");
        }
        else
        {
            SetStatus("Пользователь отклонил разрешение VPN");
        }

        _pendingConfig = null;
    }

    private void StartVpn()
    {
        if (!TryBuildConfig(out var config))
        {
            return;
        }

        var request = VpnStarter.PrepareRequest(this);
        if (request is not null)
        {
            _pendingConfig = config;
            StartActivityForResult(request, VpnRequestCode);
            return;
        }

        VpnStarter.Start(this, config ?? string.Empty);
        SetStatus("Запускаю туннель…");
    }

    /// <summary>
    /// Быстрое подключение к сервису: берём уже проверенный узел с подтверждённым доступом
    /// (сортировка скрыта в <see cref="NodeStore.PickForService"/>), сохраняем его и включаем VPN.
    /// </summary>
    private void StartForService(NodeStore.QuickService service)
    {
        NodeStore.EnsureLoaded();
        var node = NodeStore.PickForService(service);
        if (node is null)
        {
            var missing = ServiceName(service);
            SetStatus($"Нет проверенных узлов с доступом: {missing}. Откройте «Узлы» и нажмите «Проверить».");
            Toast.MakeText(this, "Нет узлов с доступом: " + missing, ToastLength.Long)?.Show();
            return;
        }

        NodeStore.Select(node);
        var config = NodeStore.BuildTunnelConfig(out var error);
        if (config is null)
        {
            SetStatus(error ?? "Не удалось собрать конфиг");
            return;
        }

        var serviceName = ServiceName(service);
        SetStatus($"{serviceName}: проверенный узел · {node.DisplayDelay}");
        var request = VpnStarter.PrepareRequest(this);
        if (request is not null)
        {
            _pendingConfig = config;
            StartActivityForResult(request, VpnRequestCode);
            return;
        }

        VpnStarter.Start(this, config);
        SetStatus($"{serviceName}: проверенный узел · запускаю туннель…");
    }

    private static string ServiceName(NodeStore.QuickService service) => service switch
    {
        NodeStore.QuickService.YouTube => "YouTube",
        NodeStore.QuickService.Telegram => "Telegram",
        NodeStore.QuickService.Discord => "Discord",
        _ => "универсальный",
    };

    private void StopVpn()
    {
        VpnStarter.Stop(this);
        SetStatus("Останавливаю туннель…");
    }

    /// <summary>
    /// Конфиг для подключения: вставленная ссылка важнее выбранного узла
    /// (удобно для проверки одного узла), иначе — выбранный узел и весь список живых.
    /// Пустой конфиг означает прямой выход: служба поднимет его сама.
    /// </summary>
    private bool TryBuildConfig(out string? config)
    {
        config = null;
        var text = _link?.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var node = LinkParser.Parse(text, "android");
            if (node is null)
            {
                SetStatus("Ссылка не разобралась");
                return false;
            }

            var settings = new AppSettings { TunnelMode = TunnelMode.Tun };
            config = SingBoxConfigBuilder.Build(
                new[] { node },
                node.Tag,
                CoreRuntime.MixedPort,
                CoreRuntime.ApiPort,
                CoreRuntime.ApiSecret,
                settings,
                // На Android локальный UDP/53 часто зарезан или подменяется — DNS идёт через узел.
                dnsThroughProxy: true);

            SetStatus("Узел из ссылки: " + node.Name + " (" + node.Protocol + ")");
            return true;
        }

        config = NodeStore.BuildTunnelConfig(out var error, preferWorking: true);
        if (config is not null)
        {
            var selected = NodeStore.Selected;
            SetStatus(selected is null
                ? "Подключаю лучший проверенный узел"
                : "Проверенный узел · " + selected.DisplayDelay);
            return true;
        }

        // Узлов ещё нет: прямой выход полезен, чтобы проверить сам туннель.
        SetStatus(error + ". Подключаю прямой выход для проверки");
        return true;
    }

    private void RefreshStatus()
    {
        _ = Task.Run(() =>
        {
            string nodeText;
            try
            {
                NodeStore.EnsureLoaded();
                var selected = NodeStore.Selected;
                var nodes = NodeStore.Nodes;
                var total = nodes.Count;
                var working = nodes.Count(item => item.Status == NodeStatus.Working);
                var universal = nodes.Count(item => item.UniversalOk == true || item.DelayMs is not null);
                var youtube = nodes.Count(item => item.YoutubeOk == true);
                var telegram = nodes.Count(item => item.TelegramOk == true);
                var discord = nodes.Count(item => item.DiscordOk == true);
                nodeText = $"В списке {total} узлов · рабочих {working}" +
                           (selected is null ? string.Empty : " · выбран " + selected.DisplayDelay) +
                           $"\nДоступ: универс. {universal}, YouTube {youtube}, " +
                           $"Telegram {telegram}, Discord {discord}";
            }
            catch (Exception exception)
            {
                AppLog.Write("android", "Не удалось прочитать узлы", exception);
                nodeText = "Узлы не прочитаны: " + exception.Message;
            }

            RunOnUiThread(() =>
            {
                SetStatus(VpnStarter.StateLabel);
                if (_node is not null)
                {
                    _node.Text = nodeText;
                }
            });
        });
    }

    /// <summary>Класс из libbox.aar: проверяем по имени, чтобы приложение собиралось и без библиотеки.</summary>
    private static bool HasLibBox()
    {
        try
        {
            // В sing-box 1.14 точка входа — статический класс libbox.Libbox
            // (класса BoxInstance из старых версий больше нет).
            Java.Lang.Class.ForName("libbox.Libbox");
            return true;
        }
        catch (Java.Lang.ClassNotFoundException)
        {
            return false;
        }
    }

    private void SetStatus(string text)
    {
        if (_status is not null)
        {
            _status.Text = text;
        }
    }
}
