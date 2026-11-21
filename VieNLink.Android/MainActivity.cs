using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Главный экран Android-версии: шапка с состоянием, вкладки Windows-клиента
/// (Подключение / Обход / Инструменты / Журнал / Общие) и быстрое подключение.
/// Узел всегда выбирается автоматически из уже проверенных.
/// </summary>
[Activity(Label = "VieNLink", MainLauncher = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTop, Theme = "@style/Theme.VieNLink")]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "vienlink")]
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "text/plain")]
public sealed partial class MainActivity : BaseActivity
{
    private const int VpnRequestCode = 1001;

    private LinearLayout? _pageHost;
    private LinearLayout? _nav;
    private readonly List<ImageView> _navIcons = new();
    private readonly List<TextView> _navLabels = new();
    private int _tab;

    private TextView? _statusText;
    private TextView? _nodeText;
    private TextView? _exitText;
    private TextView? _speedText;
    private TextView? _trafficText;
    private TextView? _stateDot;
    private TextView? _disconnect;
    private TextView? _youtubeCount;
    private TextView? _discordCount;
    private TextView? _telegramCount;
    private TextView? _universalCount;
    private TextView? _connectionStatus;

    private Handler? _handler;
    private Action? _tick;
    private DateTime _lastExitAttempt = DateTime.MinValue;
    private bool _exitBusy;
    private bool _speedMeasured;
    private string? _pendingConfig;
    private CancellationTokenSource? _trafficCts;
    private long _downBytes;
    private long _upBytes;
    private DateTime _lastTrafficUi = DateTime.MinValue;

    private static readonly string[] TabTitles = { "Подключение", "Обход", "Инструменты", "Журнал", "Общие" };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var shell = Ui.Column(this);
        shell.SetBackgroundColor(P.Background);

        var scroll = new ScrollView(this) { FillViewport = true, VerticalScrollBarEnabled = false };
        _pageHost = Ui.Column(this);
        _pageHost.SetPadding(Ui.Dp(this, 16), Ui.StatusBar(this) + Ui.Dp(this, 8), Ui.Dp(this, 16), Ui.Dp(this, 16));
        scroll.AddView(_pageHost, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        shell.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _nav = BuildNavigation();
        shell.AddView(_nav, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        SetContentView(shell);

        EnsureNotificationPermission();
        ScheduleMaintenance();

        if (Intent?.GetBooleanExtra("openNodes", false) == true)
        {
            OpenNodes();
        }

        if (Intent?.GetBooleanExtra("openTelegram", false) == true)
        {
            Open(typeof(TelegramActivity));
        }

        var initialTab = Intent?.GetIntExtra("tab", -1) ?? -1;
        if (initialTab is >= 0 and < 5)
        {
            _tab = initialTab;
        }

        HandleImport(Intent);
        HandleDebugCommands(Intent);
        SelectTab(_tab);

        // Автопроверка обновлений при запуске: релиз с более высокой версией
        // скачивается и предлагается к установке.
        UpdateCenter.StartLaunchCheck(this, Intent?.GetBooleanExtra("checkUpdate", false) == true);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;

        if (intent?.GetBooleanExtra("openNodes", false) == true)
        {
            OpenNodes();
        }

        if (intent?.GetBooleanExtra("openTelegram", false) == true)
        {
            Open(typeof(TelegramActivity));
        }

        var newTab = intent?.GetIntExtra("tab", -1) ?? -1;
        if (intent is not null && newTab is >= 0 and < 5)
        {
            SelectTab(newTab);
        }

        HandleImport(intent);
        HandleDebugCommands(intent);
        if (intent?.GetBooleanExtra("checkUpdate", false) == true)
        {
            UpdateCenter.StartLaunchCheck(this, force: true);
        }

        RefreshHeader();
    }

    protected override void OnResume()
    {
        base.OnResume();
        SelectTab(_tab);
        RefreshHeader();
        EnsureTrafficMonitor();

        _handler = new Handler(Looper.MainLooper!);
        _tick = () =>
        {
            RefreshHeader();
            EnsureTrafficMonitor();
            if (TunnelService.State == TunnelState.Running &&
                DateTime.UtcNow - _lastExitAttempt > TimeSpan.FromSeconds(30))
            {
                _ = EnsureExitAsync();
            }

            _handler?.PostDelayed(_tick, 3000);
        };
        _handler.PostDelayed(_tick, 3000);
    }

    protected override void OnPause()
    {
        StopTrafficMonitor();

        if (_handler is not null && _tick is not null)
        {
            _handler.RemoveCallbacks(_tick);
        }

        _handler = null;
        _tick = null;
        base.OnPause();
    }

    // ---------------------------------------------------------------- навигация

    private LinearLayout BuildNavigation()
    {
        var nav = Ui.Row(this);
        nav.SetPadding(Ui.Dp(this, 6), Ui.Dp(this, 6), Ui.Dp(this, 6),
            Ui.NavigationBar(this) + Ui.Dp(this, 6));
        nav.SetBackgroundColor(P.Panel);

        var icons = new[]
        {
            Resource.Drawable.ic_tab_connection,
            Resource.Drawable.ic_tab_bypass,
            Resource.Drawable.ic_tab_tools,
            Resource.Drawable.ic_tab_log,
            Resource.Drawable.ic_tab_general,
        };

        for (var index = 0; index < TabTitles.Length; index++)
        {
            var item = Ui.Column(this);
            item.SetGravity(GravityFlags.Center);
            item.SetPadding(0, Ui.Dp(this, 7), 0, Ui.Dp(this, 6));
            item.Clickable = true;
            item.Focusable = true;

            var position = index;
            item.Click += (_, _) => SelectTab(position);

            var image = new ImageView(this);
            var drawable = GetDrawable(icons[index])?.Mutate();
            if (drawable is not null)
            {
                image.SetImageDrawable(drawable);
            }

            item.AddView(image, new LinearLayout.LayoutParams(Ui.Dp(this, 22), Ui.Dp(this, 22)));
            var label = Ui.Text(this, TabTitles[index], 10.5f, P.Muted, bold: true);
            label.Gravity = GravityFlags.Center;
            label.SetPadding(0, Ui.Dp(this, 3), 0, 0);
            item.AddView(label, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

            nav.AddView(item, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            _navIcons.Add(image);
            _navLabels.Add(label);
        }

        return nav;
    }

    private void SelectTab(int index)
    {
        _tab = Math.Clamp(index, 0, TabTitles.Length - 1);
        if (_pageHost is null)
        {
            return;
        }

        _pageHost.RemoveAllViews();
        _pageHost.AddView(BuildHeader(), new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        View page = _tab switch
        {
            0 => BuildConnectionPage(),
            1 => BuildBypassPage(),
            2 => BuildToolsPage(),
            3 => BuildLogPage(),
            _ => BuildGeneralPage(),
        };
        Ui.Add(_pageHost, page, 12);
        UpdateNavigation();
        RefreshHeader();
    }

    private void UpdateNavigation()
    {
        for (var index = 0; index < _navIcons.Count; index++)
        {
            var selected = index == _tab;
            var color = selected ? P.Accent : P.Muted;
            _navIcons[index].SetColorFilter(color);
            _navLabels[index].SetTextColor(color);
            var parent = _navIcons[index].Parent as View;
            if (parent is not null)
            {
                parent.Background = selected
                    ? Ui.Rounded(P.AccentSurface, 12, this, P.AccentBorder, 0.6f)
                    : null;
            }
        }
    }

    // ------------------------------------------------------------------- шапка

    private View BuildHeader()
    {
        var card = Ui.HeaderCard(P, this);

        var top = Ui.Row(this);
        var logo = new ImageView(this);
        logo.SetScaleType(ImageView.ScaleType.CenterCrop);
        logo.SetImageResource(Resource.Mipmap.ic_launcher);
        logo.Background = Ui.Rounded(P.Inner, 12, this, P.Border);
        logo.ClipToOutline = true;
        top.AddView(logo, new LinearLayout.LayoutParams(Ui.Dp(this, 46), Ui.Dp(this, 46)));

        var titles = Ui.Column(this);
        titles.SetPadding(Ui.Dp(this, 12), 0, 0, 0);
        var nameRow = Ui.Row(this);
        nameRow.AddView(Ui.Text(this, "VieNLink", 21f, P.TextStrong, bold: true));
        nameRow.AddView(Ui.Badge(this, P, "БЕТА", P.Warning));
        titles.AddView(nameRow);
        var subtitle = Ui.Text(this,
            (StoreBuildInfo.IsStoreBuild
                ? "Прокси и подключение · v"
                : "Прокси, обход блокировок и Telegram · v") + UpdateCenter.CurrentVersion(this),
            11.5f, P.Muted);
        subtitle.SetPadding(0, Ui.Dp(this, 2), 0, 0);
        titles.AddView(subtitle);
        top.AddView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(top);

        var statusRow = Ui.Row(this);
        statusRow.SetPadding(0, Ui.Dp(this, 14), 0, 0);
        _stateDot = Ui.Text(this, "●", 15f, P.Muted);
        statusRow.AddView(_stateDot);
        _statusText = Ui.Text(this, "VPN не подключён", 15f, P.TextStrong, bold: true);
        _statusText.SetPadding(Ui.Dp(this, 7), 0, 0, 0);
        statusRow.AddView(_statusText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _disconnect = Ui.Pill(this, P, "Отключить", UiButtonKind.Danger, StopEverything, Resource.Drawable.ic_power);
        _disconnect.Visibility = ViewStates.Gone;
        statusRow.AddView(_disconnect);
        card.AddView(statusRow);

        _nodeText = Ui.Text(this, "Узел не выбран", 13f, P.MutedStrong);
        _nodeText.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        card.AddView(_nodeText);

        var metrics = Ui.Row(this);
        metrics.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        _exitText = Ui.Text(this, "Выход: —", 11.5f, P.Muted);
        _speedText = Ui.Text(this, "Скорость: —", 11.5f, P.Muted);
        metrics.AddView(_exitText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        metrics.AddView(_speedText);
        card.AddView(metrics);

        _trafficText = Ui.Text(this, "Трафик: —", 11.5f, P.Muted);
        _trafficText.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        card.AddView(_trafficText);

        // Мини-иконки сервисов с числом проверенных узлов, которые к ним подключаются.
        var counters = Ui.Row(this);
        counters.SetPadding(0, Ui.Dp(this, 10), 0, 0);
        counters.AddView(ServiceCounter(Resource.Drawable.ic_service_youtube_brand, P.YouTube, out _youtubeCount,
                tintIcon: false),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        counters.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 6), 1));
        counters.AddView(ServiceCounter(Resource.Drawable.ic_service_discord, P.Discord, out _discordCount),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        counters.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 6), 1));
        counters.AddView(ServiceCounter(Resource.Drawable.ic_service_telegram, P.Telegram, out _telegramCount),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        counters.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 6), 1));
        counters.AddView(ServiceCounter(Resource.Drawable.ic_service_universal, P.Universal, out _universalCount),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(counters);

        var actions = Ui.Row(this);
        actions.SetPadding(0, Ui.Dp(this, 12), 0, 0);
        // Главная удобная кнопка шапки: ищет и проверяет непроверенные узлы,
        // не заставляя идти во вкладку «Инструменты».
        _findButton = Ui.Pill(this, P, _finding ? "Проверяю узлы…" : "Проверить узлы", UiButtonKind.Accent,
            () => _ = FindNodesAsync(), Resource.Drawable.ic_nodes);
        var measure = Ui.Pill(this, P, "Скорость", UiButtonKind.Ghost, () => _ = RunSpeedAsync(false),
            Resource.Drawable.ic_speed);
        _nextNodeButton = Ui.Pill(this, P, "Дальше", UiButtonKind.Accent,
            () => _ = SwitchToNextNodeAsync(), Resource.Drawable.ic_refresh);
        _nextNodeButton.Visibility = ViewStates.Gone;
        actions.AddView(_findButton, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.2f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(measure, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.9f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(_nextNodeButton, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.9f));
        card.AddView(actions);

        _connectionStatus = Ui.Hint(this, "Загружаю узлы…", P);
        _connectionStatus.SetPadding(0, Ui.Dp(this, 10), 0, 0);
        card.AddView(_connectionStatus);

        return card;
    }

    /// <summary>
    /// Мини-иконка сервиса и число проверенных узлов с доступом к нему:
    /// пользователь сразу видит, сколько серверов готово для каждой кнопки.
    /// </summary>
    private LinearLayout ServiceCounter(int icon, Color tone, out TextView? count, bool tintIcon = true)
    {
        var chip = Ui.Row(this);
        chip.SetPadding(Ui.Dp(this, 6), Ui.Dp(this, 4), Ui.Dp(this, 6), Ui.Dp(this, 4));
        chip.Background = Ui.Rounded(Color.Argb(28, tone.R, tone.G, tone.B), 10, this,
            Color.Argb(90, tone.R, tone.G, tone.B));

        var image = new ImageView(this);
        var drawable = Ui.Icon(this, icon);
        if (drawable is not null)
        {
            if (tintIcon)
            {
                drawable.SetTint(tone);
            }

            image.SetImageDrawable(drawable);
        }

        chip.AddView(image, new LinearLayout.LayoutParams(Ui.Dp(this, 15), Ui.Dp(this, 15)));

        count = Ui.Text(this, "—", 12f, tone, bold: true);
        count.SetPadding(Ui.Dp(this, 5), 0, 0, 0);
        chip.AddView(count, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        return chip;
    }

    /// <summary>Счётчики сервисов: рабочие узлы с подтверждённым доступом.</summary>
    private void UpdateServiceCounters(
        (int Total, int Working, int Universal, int Youtube, int Telegram, int Discord) summary)
    {
        if (_youtubeCount is not null)
        {
            _youtubeCount.Text = summary.Youtube.ToString();
        }

        if (_discordCount is not null)
        {
            _discordCount.Text = summary.Discord.ToString();
        }

        if (_telegramCount is not null)
        {
            _telegramCount.Text = summary.Telegram.ToString();
        }

        if (_universalCount is not null)
        {
            // Старый кэш не знает UniversalOk: универсальным считаем узел с задержкой.
            _universalCount.Text = summary.Universal.ToString();
        }
    }

    private void RefreshHeader()
    {
        if (_statusText is null)
        {
            return;
        }

        var state = TunnelService.State;
        var running = state == TunnelState.Running;
        var serviceStarting = state == TunnelState.Starting;
        // Пока идёт поиск и проверка узла служба ещё не запущена — для человека это тоже «подключаюсь».
        var connecting = serviceStarting || _connecting;
        var bypass = TunnelService.IsBypassMode;

        _statusText.Text = running
            ? bypass ? "TUN-обход (бета)" : "Подключено"
            : connecting
                ? bypass ? "Запускаю TUN-обход (бета)…" : "Подключаюсь…"
                : "VPN не подключён";
        if (_stateDot is not null)
        {
            _stateDot.SetTextColor(running ? P.Positive : connecting ? P.Warning : P.Muted);
        }

        if (_disconnect is not null)
        {
            // «Отключить» видно и во время поиска узла: нажатие прерывает поиск.
            _disconnect.Visibility = running || serviceStarting || _connecting || CoreRuntime.IsRunning
                ? ViewStates.Visible
                : ViewStates.Gone;
        }

        if (_nextNodeButton is not null)
        {
            // «Дальше» — переключение на следующий проверенный узел без разрыва туннеля.
            _nextNodeButton.Visibility = running ? ViewStates.Visible : ViewStates.Gone;
            _nextNodeButton.Enabled = !_connecting;
        }

        var selected = NodeStore.Selected;
        var rating = Rating(selected?.DelayMs);
        if (_nodeText is not null)
        {
            if (bypass)
            {
                _nodeText.Text = "Прокси-узел не используется · стратегия TUN-обхода";
            }
            else if (selected is null)
            {
                _nodeText.Text = connecting
                    ? "Выбираю проверенный узел…"
                    : "Узел не выбран — нажмите кнопку сервиса";
            }
            else
            {
                var speed = selected.SpeedMbps is { } mbps ? $" · {mbps:0.#} Мбит/с" : string.Empty;
                _nodeText.Text = $"Проверенный узел · {selected.DisplayDelay}{speed} · {new string('★', rating)}{new string('☆', 5 - rating)}";
            }
        }

        if (_exitText is not null)
        {
            var show = NodeStore.Settings.ShowExitInfo;
            _exitText.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            var exit = NetworkProbe.CachedExit;
            _exitText.Text = exit is null ? "Выход: —" : "Выход: " + exit.Short;
        }

        if (_speedText is not null)
        {
            var show = NodeStore.Settings.ShowTraffic;
            _speedText.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            var speed = NetworkProbe.CachedSpeed ?? NodeStore.Selected?.SpeedMbps;
            _speedText.Text = speed is null ? "Скорость: —" : $"Скорость: {speed:0.#} Мбит/с";
        }

        if (_trafficText is not null)
        {
            var show = NodeStore.Settings.ShowTraffic;
            _trafficText.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            _trafficText.Text = _downBytes == 0 && _upBytes == 0
                ? "Трафик: —"
                : $"Трафик: ↓ {FormatBytes(_downBytes)} · ↑ {FormatBytes(_upBytes)}";
        }

        if (_connectionStatus is not null)
        {
            if (!NodeStore.IsLoaded)
            {
                _connectionStatus.Text = _headerDetail is not null &&
                                         (_finding || DateTime.UtcNow < _headerDetailUntil)
                    ? _headerDetail
                    : "Загружаю узлы…";
                _ = Task.Run(() =>
                {
                    try
                    {
                        NodeStore.EnsureLoaded();
                        RunOnUiThread(RefreshHeader);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("android", "Не удалось прочитать узлы", exception);
                        RunOnUiThread(() => _connectionStatus!.Text = "Узлы не прочитаны: " + exception.Message);
                    }
                });
                return;
            }

            try
            {
                var summary = NodeStore.Summary();
                UpdateServiceCounters(summary);

                // «Рабочих» и «универсальных» отдельно не пишем: это уже видно по иконкам сервисов.
                // Во время подключения показываем ход поиска узла, а не сводку.
                // Фоновая проверка тоже видна: без этого казалось, что приложение молчит.
                var countText = $"В списке {summary.Total} узлов";
                var probeActivity = ProbeClient.ActivityText;
                _connectionStatus.Text = _connecting && _connectText is not null
                    ? _connectText
                    : _headerDetail is not null && (_finding || DateTime.UtcNow < _headerDetailUntil)
                        ? _headerDetail
                        : probeActivity ?? countText;
                if (_findButton is not null && !_connecting)
                {
                    _findButton.Text = _finding || (ProbeClient.IsRunning && !ProbeClient.IsPaused)
                        ? "Остановить поиск"
                        : "Проверить узлы";
                }
            }
            catch (Exception exception)
            {
                _connectionStatus.Text = "Узлы не прочитаны: " + exception.Message;
            }
        }
    }

    private static int Rating(int? delayMs) => delayMs switch
    {
        null => 0,
        <= 100 => 5,
        <= 250 => 4,
        <= 600 => 3,
        <= 1200 => 2,
        _ => 1,
    };

    /// <summary>
    /// Поток /traffic Clash API: байты за последнюю секунду. Считаем суммарный трафик
    /// сессии, как в шапке Windows, пока экран активен.
    /// </summary>
    private void EnsureTrafficMonitor()
    {
        if (_trafficCts is not null ||
            TunnelService.State != TunnelState.Running ||
            !CoreRuntime.IsRunning ||
            !NodeStore.Settings.ShowTraffic)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _trafficCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
                await api.StreamTrafficAsync((up, down) =>
                {
                    Interlocked.Add(ref _upBytes, up);
                    Interlocked.Add(ref _downBytes, down);
                    if (DateTime.UtcNow - _lastTrafficUi > TimeSpan.FromSeconds(1))
                    {
                        _lastTrafficUi = DateTime.UtcNow;
                        RunOnUiThread(RefreshHeader);
                    }
                }, cts.Token);
            }
            catch (System.OperationCanceledException)
            {
                // Поток остановлен при уходе с экрана или отключении.
            }
            catch (Exception exception)
            {
                AppLog.Write("traffic", "Поток трафика Clash API не удался", exception);
            }
            finally
            {
                if (ReferenceEquals(_trafficCts, cts))
                {
                    _trafficCts = null;
                }

                cts.Dispose();
            }
        });
    }

    private void StopTrafficMonitor()
    {
        try
        {
            _trafficCts?.Cancel();
        }
        catch (Exception)
        {
            // Поток мог уже закрыться.
        }

        _trafficCts = null;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " Б";
        }

        if (bytes < 1024L * 1024)
        {
            return (bytes / 1024.0).ToString("0.#") + " КБ";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return (bytes / 1024.0 / 1024.0).ToString("0.#") + " МБ";
        }

        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.##") + " ГБ";
    }

    private async Task EnsureExitAsync()    {
        if (_exitBusy || TunnelService.State != TunnelState.Running)
        {
            return;
        }

        var showExit = NodeStore.Settings.ShowExitInfo;
        var showSpeed = NodeStore.Settings.ShowTraffic;
        if (!showExit && !showSpeed)
        {
            return;
        }

        _exitBusy = true;
        _lastExitAttempt = DateTime.UtcNow;
        try
        {
            if (showExit)
            {
                var info = await NetworkProbe.LookupExitAsync(CancellationToken.None);
                if (info is not null)
                {
                    RunOnUiThread(RefreshHeader);
                }
            }

            // Скорость меряем независимо от страны выхода: один запрос через узел.
            if (showSpeed && !_speedMeasured)
            {
                _speedMeasured = true;
                var speed = await NetworkProbe.MeasureSpeedAsync(CancellationToken.None);
                if (speed is not null)
                {
                    RunOnUiThread(RefreshHeader);
                }
            }
        }
        finally
        {
            _exitBusy = false;
        }
    }

    private async Task RunSpeedAsync(bool silent)
    {
        if (TunnelService.State != TunnelState.Running || !CoreRuntime.IsRunning)
        {
            if (!silent)
            {
                Ui.Toast(this, "Сначала подключитесь к узлу");
            }

            return;
        }

        if (!silent)
        {
            SetConnectionStatus("Замеряю скорость через узел…");
        }

        var speed = await NetworkProbe.MeasureSpeedAsync(CancellationToken.None);
        RunOnUiThread(() =>
        {
            RefreshHeader();
            if (!silent)
            {
                Ui.Toast(this, speed is null ? "Замер не удался" : $"Скорость: {speed:0.#} Мбит/с");
                SetConnectionStatus(speed is null ? "Замер не удался" : $"Скорость: {speed:0.#} Мбит/с");
            }
        });
    }

    // ------------------------------------------------------------ поиск узлов

    /// <summary>
    /// Кнопка шапки «Проверить узлы»: проверяет ещё не проверенные узлы в процессе :probe
    /// и показывает ход прямо в шапке, не уводя на другие вкладки. Повторное нажатие
    /// во время поиска останавливает и текущую партию, и фоновый поиск.
    /// </summary>
    private async Task FindNodesAsync()
    {
        if (_finding || ProbeClient.IsRunning)
        {
            ProbeClient.Cancel();
            _headerDetail = "Поиск узлов остановлен";
            _headerDetailUntil = DateTime.UtcNow.AddSeconds(15);
            UpdateFindButton("Проверить узлы");
            RefreshHeader();
            return;
        }

        // Явное действие пользователя снимает прежнюю остановку.
        ProbeClient.Resume();
        _finding = true;
        _headerDetail = "Проверяю узлы: готовлю проверку…";
        UpdateFindButton("Остановить поиск");
        RefreshHeader();

        try
        {
            await Task.Run(() => NodeStore.EnsureLoaded());
            var progress = new Progress<ProbeProgress>(state => RunOnUiThread(() =>
            {
                _headerDetail = state.Total > 0
                    ? $"Проверяю узлы: {state.Tested} из {state.Total} · рабочих {state.Working}"
                    : "Проверяю узлы…";
                RefreshHeader();
            }));

            // recheckWorking: false — ищем именно непроверенные узлы, не тратя трафик
            // на только что проверенные рабочие.
            var summary = await ProbeClient.RunBatchAsync(
                NodeTester.MaxBatch, CancellationToken.None, progress, recheckWorking: false);

            _headerDetail = summary.Error is not null
                ? summary.Error
                : $"Проверено {summary.Tested}: рабочих {summary.Working}";
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Проверка узлов из шапки не удалась", exception);
            _headerDetail = "Проверка узлов не удалась: " + exception.Message;
        }
        finally
        {
            _finding = false;
            _headerDetailUntil = DateTime.UtcNow.AddSeconds(20);
            UpdateFindButton("Проверить узлы");
            RefreshHeader();
        }
    }

    private void UpdateFindButton(string text) => RunOnUiThread(() =>
    {
        if (_findButton is not null)
        {
            _findButton.Text = text;
        }
    });

    /// <summary>Временная строка в шапке: ход операции или её итог.</summary>
    private void ShowHeaderDetail(string text, int holdSeconds = 20)
    {
        _headerDetail = text;
        _headerDetailUntil = DateTime.UtcNow.AddSeconds(Math.Max(1, holdSeconds));
        RefreshHeader();
    }

    // ------------------------------------------------------------- подключение

    private bool _connecting;
    private string? _connectText;
    private CancellationTokenSource? _connectCts;
    private bool _finding;
    private bool _switchingNode;
    private string? _headerDetail;
    private DateTime _headerDetailUntil = DateTime.MinValue;
    private TextView? _findButton;
    private TextView? _nextNodeButton;

    /// <summary>
    /// Сколько партий по 150 узлов проверять, пока не найдётся узел с доступом к сервису.
    /// Узлы проверяются без повторов, поэтому следующее нажатие продолжит с этого места.
    /// </summary>
    private const int FindForServiceBatches = 10;

    /// <summary>
    /// Кнопка «Дальше»: переключает на следующий проверенный узел, отсортированный по пингу.
    /// Если узел уже в текущем конфиге — переключение через Clash selector, без перезапуска
    /// TUN и без разрыва уже открытых приложений.
    /// </summary>
    private async Task SwitchToNextNodeAsync()
    {
        if (_switchingNode || _connecting)
        {
            return;
        }

        if (TunnelService.State != TunnelState.Running || !CoreRuntime.IsRunning)
        {
            Ui.Toast(this, "Сначала подключитесь к узлу");
            return;
        }

        _switchingNode = true;
        try
        {
            var now = DateTime.UtcNow;
            var settings = NodeStore.Settings;
            var ordered = NodeStore.Snapshot()
                .Where(node => NodeAvailability.IsUsable(
                    node, NodeLimits.WorkingRecheckMinutes, settings.ConnectOnlyMine, now))
                .OrderBy(node => node.DelayMs ?? int.MaxValue)
                .ThenByDescending(node => node.IsMine)
                .ThenByDescending(node => node.OpenDomains)
                .ToList();
            if (ordered.Count == 0)
            {
                Ui.Toast(this, "Нет проверенных узлов: нажмите «Проверить узлы»");
                return;
            }

            var currentId = NodeStore.Selected?.Id;
            var index = ordered.FindIndex(node => node.Id == currentId);
            var next = ordered[(index + 1) % ordered.Count];
            if (currentId == next.Id)
            {
                Ui.Toast(this, "Других проверенных узлов нет");
                return;
            }

            ShowHeaderDetail("Переключаю узел…", 6);
            if (CoreRuntime.LoadedTags.Contains(next.Tag))
            {
                using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
                await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, next.Tag, CancellationToken.None);
                NodeStore.Select(next);
                TunnelService.UpdateNodeLabel(next.DisplayDelay);
                _speedMeasured = false;
                ShowHeaderDetail("Узел переключён · " + next.DisplayDelay, 10);
                Ui.Toast(this, "Узел переключён · " + next.DisplayDelay);
                ScheduleExitProbe();
                return;
            }

            NodeStore.Select(next);
            var config = NodeStore.BuildTunnelConfig(out var error);
            if (config is null)
            {
                Ui.Toast(this, error ?? "Не удалось собрать конфиг");
                return;
            }

            _speedMeasured = false;
            ShowHeaderDetail("Переключаю узел…", 6);
            StartWithConfig(config);
        }
        catch (Exception exception)
        {
            AppLog.Write("health", "Переключение на следующий узел не удалось", exception);
            Ui.Toast(this, "Не удалось переключить узел");
        }
        finally
        {
            _switchingNode = false;
            RefreshHeader();
        }
    }

    private async void StartForService(NodeStore.QuickService service)
    {
        if (_connecting)
        {
            Ui.Toast(this, "Подключение уже идёт");
            return;
        }

        _connecting = true;
        var cts = new CancellationTokenSource();
        _connectCts = cts;
        RefreshHeader();
        try
        {
            // Кнопка сервиса — явное действие: снимаем прежнюю остановку поиска.
            ProbeClient.Resume();
            NodeStore.EnsureLoaded();
            var name = ServiceName(service);
            var progress = new Progress<string>(ReportConnect);
            ReportConnect("Подключаюсь: ищу проверенный узел…");
            var node = await NodeStore.PickVerifiedAsync(service, null, progress, cts.Token);

            if (node is null)
            {
                // Готового узла нет: проверяем непроверенные узлы партиями, пока не найдётся
                // рабочий именно с доступом к выбранному сервису.
                node = await FindNodeForServiceAsync(service, name, cts.Token);
            }

            if (node is null)
            {
                ReportConnect(cts.IsCancellationRequested
                    ? "Поиск узла отменён"
                    : $"Рабочий узел с доступом к {name} не нашёлся");
                if (!cts.IsCancellationRequested)
                {
                    Ui.Toast(this, $"Рабочий узел для {name} не нашёлся — попробуйте ещё раз позже");
                }

                return;
            }

            // Туннель уже работает и узел загружен — переключаемся без перезапуска TUN.
            if (TunnelService.State == TunnelState.Running && CoreRuntime.IsRunning &&
                CoreRuntime.LoadedTags.Contains(node.Tag))
            {
                using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
                await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, node.Tag, cts.Token);
                TunnelService.UpdateNodeLabel(node.DisplayDelay);
                ReportConnect($"{name}: подключено · {node.DisplayDelay}");
                Ui.Toast(this, "Подключено · " + node.DisplayDelay);
                return;
            }

            var config = NodeStore.BuildTunnelConfig(out var error);
            if (config is null)
            {
                ReportConnect(error ?? "Не удалось собрать конфиг");
                return;
            }

            _speedMeasured = false;
            ReportConnect($"{name}: живой узел · {node.DisplayDelay}");
            StartWithConfig(config);
        }
        catch (System.OperationCanceledException)
        {
            ReportConnect("Поиск узла отменён");
        }
        catch (Exception exception)
        {
            AppLog.Write("android", "Быстрое подключение не удалось", exception);
            ReportConnect("Ошибка: " + exception.Message);
        }
        finally
        {
            _connecting = false;
            _connectText = null;
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
            }

            cts.Dispose();
            // Не перерисовываем шапку синхронно: служба ещё не успела перейти в «подключаюсь»,
            // и статус мигнул бы на «VPN не подключён». Через мгновение состояние уже настоящее.
            ScheduleHeaderRefresh();
        }
    }

    /// <summary>
    /// Проверяет партии непроверенных узлов, пока не появится рабочий узел с доступом
    /// к сервису. Уже проверенные узлы не повторяются, поэтому список продвигается вперёд.
    /// </summary>
    private async Task<ProxyNode?> FindNodeForServiceAsync(
        NodeStore.QuickService service,
        string name,
        CancellationToken cancellationToken)
    {
        var batches = 0;
        for (var attempt = 0; batches < FindForServiceBatches; attempt++)
        {
            if (attempt > FindForServiceBatches * 6)
            {
                // Долго ждём чужую проверку (фон/другая кнопка) — не держим пользователя вечно.
                return null;
            }

            ReportConnect($"Нет готового узла для {name}: проверяю узлы, партия {batches + 1} из {FindForServiceBatches}…");
            var progress = new Progress<ProbeProgress>(state =>
                ReportConnect($"Ищу узел для {name} — {state.Describe()}"));
            var batchStarted = DateTime.UtcNow;
            var batch = await ProbeClient.RunBatchAsync(
                NodeTester.MaxBatch, cancellationToken, progress, recheckWorking: false);

            if (batch.Error == ProbeClient.BusyError)
            {
                // Параллельная проверка (фоновая) — подождём её и продолжим.
                await Task.Delay(2000, cancellationToken);
                continue;
            }

            if (batch.Tested == 0)
            {
                ReportConnect(batch.Error ?? "Непроверенных узлов не осталось");
                return null;
            }

            batches++;
            // Только узел, проверенный в этой партии: прежний «рабочий» мог уже умереть.
            var found = ProbeClient.PickFreshForService(service, batchStarted);
            if (found is not null)
            {
                return found;
            }

            ReportConnect($"{name}: в партии {batches} подходящих узлов нет, проверяю дальше…");
        }

        return null;
    }

    private void ReportConnect(string text)
    {
        _connectText = text;
        SetConnectionStatus(text);
    }

    private void StartWithConfig(string config)
    {
        _downBytes = 0;
        _upBytes = 0;
        NetworkProbe.Invalidate();
        if (_trafficText is not null)
        {
            _trafficText.Text = "Трафик: —";
        }
        var request = VpnStarter.PrepareRequest(this);
        if (request is not null)
        {
            _pendingConfig = config;
            StartActivityForResult(request, VpnRequestCode);
            return;
        }

        VpnStarter.Start(this, config);
        ScheduleHeaderRefresh();
        ScheduleExitProbe();
    }

    /// <summary>
    /// Служба поднимает TUN не мгновенно: обновляем шапку чуть позже,
    /// чтобы «Подключаюсь…» появлялось сразу, а не через такт таймера.
    /// </summary>
    private void ScheduleHeaderRefresh()
    {
        if (_handler is null)
        {
            return;
        }

        _handler.PostDelayed(() =>
        {
            RefreshHeader();
            EnsureTrafficMonitor();
        }, 700);
    }

    private void ScheduleExitProbe()
    {
        _lastExitAttempt = DateTime.MinValue;
        _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            await EnsureExitAsync();
        });
    }

    private void StopEverything()
    {
        // Долгий поиск узла тоже прерываем: иначе он продолжился бы после «Отключить».
        try
        {
            _connectCts?.Cancel();
        }
        catch (Exception)
        {
            // Источник отмены мог уже освободиться.
        }

        if (_connecting)
        {
            // Проверка перед подключением идёт в :probe — гасим и её,
            // иначе она продолжила бы тратить трафик после отмены.
            ProbeClient.Cancel();
        }

        StopTrafficMonitor();
        VpnStarter.Stop(this);
        SetConnectionStatus("Останавливаю туннель…");
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != VpnRequestCode)
        {
            return;
        }

        if (resultCode == Result.Ok && _pendingBypass is not null)
        {
            VpnStarter.StartBypass(this, _pendingBypass.Value);
            SetConnectionStatus("Запускаю TUN-обход…");
            ScheduleHeaderRefresh();
            _pendingBypass = null;
            _pendingConfig = null;
            return;
        }

        if (resultCode == Result.Ok && _pendingConfig is not null)
        {
            VpnStarter.Start(this, _pendingConfig);
            SetConnectionStatus("Подключаюсь…");
            ScheduleHeaderRefresh();
            ScheduleExitProbe();
        }
        else
        {
            SetConnectionStatus("Пользователь отклонил разрешение VPN");
        }

        _pendingConfig = null;
        _pendingBypass = null;
    }

    private static string ServiceName(NodeStore.QuickService service) => service switch
    {
        NodeStore.QuickService.YouTube => "YouTube",
        NodeStore.QuickService.Telegram => "Telegram",
        NodeStore.QuickService.Discord => "Discord",
        _ => "универсальный",
    };

    private void SetConnectionStatus(string text)
    {
        RunOnUiThread(() =>
        {
            if (_connectionStatus is not null)
            {
                _connectionStatus.Text = text;
            }
        });
    }

    // ---------------------------------------------------------------- импорт

    private void HandleImport(Intent? intent)
    {
        var payload = ExtractImport(intent);
        if (payload is null)
        {
            return;
        }

        SetConnectionStatus("Импортирую ссылку…");
        _ = Task.Run(async () =>
        {
            try
            {
                NodeStore.EnsureLoaded();
                var before = NodeStore.SnapshotSources().Select(source => source.Id).ToHashSet();
                var message = NodeStore.AddSource(payload, null, mine: false);
                var created = NodeStore.SnapshotSources().FirstOrDefault(source => !before.Contains(source.Id));
                RunOnUiThread(() => SetConnectionStatus(message));
                if (created is null)
                {
                    RunOnUiThread(() => Ui.Toast(this, message));
                    return;
                }

                var progress = new Progress<string>(text => RunOnUiThread(() => SetConnectionStatus(text)));
                var loaded = await NodeStore.ImportSourceAsync(created, progress, CancellationToken.None);
                RunOnUiThread(() =>
                {
                    Ui.Toast(this, loaded);
                    Open(typeof(SourcesActivity));
                });
            }
            catch (Exception exception)
            {
                AppLog.Write("android", "Не удалось импортировать ссылку", exception);
                RunOnUiThread(() => SetConnectionStatus("Импорт не удался: " + exception.Message));
            }
        });
    }

    private static string? ExtractImport(Intent? intent)
    {
        if (intent is null)
        {
            return null;
        }

        // Отладочная команда: adb … -e link "vless://…" (или linkB64). Поле ввода убрано,
        // поэтому такая ссылка сразу становится источником и попадает в список узлов.
        var extra = intent.GetStringExtra("link")?.Trim();
        if (!string.IsNullOrWhiteSpace(extra))
        {
            return extra;
        }

        var encoded = intent.GetStringExtra("linkB64");
        if (!string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Trim();
            }
            catch (FormatException)
            {
                return null;
            }
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

    private void OpenNodes()
    {
        var nodes = new Intent(this, typeof(NodesActivity));
        foreach (var key in new[] { "refresh", "test" })
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

    private void ScheduleMaintenance()
    {
        Maintenance.EnsureScheduled(this);
        _ = Task.Run(async () =>
        {
            try
            {
                await Maintenance.RunAsync(this);
                await Task.Run(() => ProbeScheduler.Start(this));
            }
            catch (Exception exception)
            {
                AppLog.Write("maintenance", "Стартовое обслуживание не удалось", exception);
            }
        });
    }
}

/// <summary>Идентификаторы общих иконок для мест, где имя ресурса нужно явно.</summary>
internal static class DrawableIds
{
    public static int Bolt => Resource.Drawable.ic_bolt;
}
