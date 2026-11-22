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
/// Сервер всегда выбирается автоматически из уже проверенных.
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
    private TextView? _shareButton;
    private TextView? _addServerButton;
    private LinearLayout? _actionsRow2;

    private Handler? _handler;
    private Action? _tick;
    private DateTime _lastExitAttempt = DateTime.MinValue;
    private bool _exitFailed;
    private bool _exitBusy;
    private bool _speedMeasured;
    private string? _pendingConfig;
    private CancellationTokenSource? _trafficCts;
    private long _downBytes;
    private long _upBytes;
    private DateTime _lastTrafficUi = DateTime.MinValue;

    private static readonly string[] TabTitles = { "Подключение", "Обход", "Инструменты", "Журнал" };

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
        if (initialTab is >= 0 and < 4)
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
        if (intent is not null && newTab is >= 0 and < 4)
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
        _ = EnsureFreshCountersAsync();

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

    /// <summary>
    /// После возврата в приложение счётчики не должны висеть нулями до пятиминутной
    /// перепроверки: сразу проверяем ранее рабочие серверы одной небольшой партией.
    /// </summary>
    private async Task EnsureFreshCountersAsync()
    {
        try
        {
            if (SettingsStore.Current.AutomationPaused || ProbeClient.IsPaused || ProbeClient.IsRunning)
            {
                return;
            }

            await Task.Run(() => NodeStore.EnsureLoaded());
            if (NodeStore.Summary().Working >= NodeLimits.MinWorkingNodes)
            {
                return;
            }

            // workingOnly: только серверы, которые уже были рабочими и чья проверка истекла.
            var summary = await ProbeClient.RunBatchAsync(20, CancellationToken.None, workingOnly: true);
            if (summary.Tested > 0)
            {
                RefreshHeader();
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Не удалось быстро обновить счётчики при возврате", exception);
        }
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
            _ => BuildLogPage(),
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

        var nodeRow = Ui.Row(this);
        _nodeText = Ui.Text(this, "Сервер не выбран", 13f, P.MutedStrong);
        _nodeText.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        nodeRow.AddView(_nodeText);
        card.AddView(nodeRow);

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

        // Мини-иконки сервисов с числом проверенных серверов, которые к ним подключаются.
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
        actions.SetPadding(0, Ui.Dp(this, 14), 0, 0);
        // Главное действие — на всю ширину, ниже два оформленных действия рядом.
        _findButton = Ui.Pill(this, P, _finding ? "Проверяю серверы…" : "Проверить серверы",
            UiButtonKind.Primary, () => _ = FindNodesAsync(), Resource.Drawable.ic_nodes);
        CompactWideHeaderButton(_findButton);
        actions.AddView(_findButton, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        card.AddView(actions);

        _actionsRow2 = Ui.Row(this);
        _actionsRow2.SetPadding(0, Ui.Dp(this, 10), 0, 0);
        _nextNodeButton = Ui.Pill(this, P, "Сменить сервер", UiButtonKind.Accent,
            () => _ = SwitchToNextNodeAsync(), Resource.Drawable.ic_refresh);
        _shareButton = Ui.Pill(this, P, "Поделиться сервером", UiButtonKind.Secondary,
            ShareCurrentNode, Resource.Drawable.ic_export);
        CompactWrapHeaderButton(_nextNodeButton);
        CompactWrapHeaderButton(_shareButton);
        // Оформление: главное действие — зелёный градиент, смена сервера — синяя,
        // «Поделиться» — фиолетовая; значки в тон текста.
        StyleHeaderAction(_findButton, P.Accent, filled: true);
        StyleHeaderAction(_nextNodeButton, P.Telegram, filled: false);
        StyleHeaderAction(_shareButton, P.Discord, filled: false);
        _actionsRow2.AddView(_nextNodeButton, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _actionsRow2.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        _actionsRow2.AddView(_shareButton, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(_actionsRow2);

        // Ручное добавление — отдельной строкой под двумя кнопками: в тесном ряду
        // из трёх подпись «Поделиться сервером» ломалась посреди слова.
        var addRow = Ui.Row(this);
        addRow.SetPadding(0, Ui.Dp(this, 8), 0, 0);
        _addServerButton = Ui.Pill(this, P, "Добавить сервер", UiButtonKind.Accent,
            ShowAddServerDialog, Resource.Drawable.ic_add);
        CompactWideHeaderButton(_addServerButton);
        StyleHeaderAction(_addServerButton, P.Positive, filled: false);
        addRow.AddView(_addServerButton, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        card.AddView(addRow);

        _connectionStatus = Ui.Hint(this, "Загружаю серверы…", P);
        _connectionStatus.SetPadding(0, Ui.Dp(this, 10), 0, 0);
        card.AddView(_connectionStatus);

        return card;
    }

    /// <summary>Кнопка с длинной подписью: две строки, перенос только по словам.</summary>
    private void CompactWrapHeaderButton(TextView button)
    {
        button.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 4), Ui.Dp(this, 8), Ui.Dp(this, 4));
        button.TextSize = 12f;
        button.SetSingleLine(false);
        button.SetMaxLines(2);
        button.Gravity = Android.Views.GravityFlags.Center;
        button.SetMinHeight(Ui.Dp(this, 48));
        // Без переносов по слогам: «Подел/иться» выглядело как ошибка.
        button.BreakStrategy = Android.Text.BreakStrategy.Balanced;
        button.HyphenationFrequency = Android.Text.HyphenationFrequency.None;
    }

    /// <summary>Кнопка главного действия на всю ширину.</summary>
    private void CompactWideHeaderButton(TextView button)
    {
        button.TextSize = 13.5f;
        button.SetSingleLine(true);
        button.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        button.SetMinHeight(Ui.Dp(this, 48));
    }

    /// <summary>Градиент и цвет значка для кнопки шапки.</summary>
    private void StyleHeaderAction(TextView button, Color tone, bool filled)
    {
        var from = filled ? tone : Ui.Blend(P.Card, tone, 0.20f);
        var to = filled ? Ui.Blend(tone, Color.Black, 0.30f) : Ui.Blend(P.Card, tone, 0.04f);
        var pressedFrom = filled ? Ui.Blend(tone, Color.White, 0.14f) : Ui.Blend(P.Card, tone, 0.30f);
        var pressedTo = filled ? Ui.Blend(tone, Color.Black, 0.16f) : Ui.Blend(P.Card, tone, 0.18f);
        var border = filled
            ? Ui.Blend(tone, Color.White, 0.28f)
            : Color.Argb(150, tone.R, tone.G, tone.B);
        button.Background = Ui.PressableGradient(this, from, to, pressedFrom, pressedTo, 16, border);
        var textColor = filled ? P.AccentContrast : tone;
        button.SetTextColor(textColor);
        try
        {
            button.GetCompoundDrawables()?.FirstOrDefault()?.SetTint(textColor);
        }
        catch (Exception exception)
        {
            AppLog.Write("android", "Не удалось перекрасить значок кнопки", exception);
        }
    }

    /// <summary>
    /// Мини-иконка сервиса и число проверенных серверов с доступом к нему:
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

    /// <summary>Счётчики сервисов: рабочие серверы с подтверждённым доступом.</summary>
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
            // Старый кэш не знает UniversalOk: универсальным считаем сервер с задержкой.
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
        // Пока идёт поиск и проверка сервера служба ещё не запущена — для человека это тоже «подключаюсь».
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
            // «Отключить» видно и во время поиска сервера: нажатие прерывает поиск.
            // Пока только подключаемся (туннеля ещё нет), честнее подпись «Отмена».
            _disconnect.Text = _connecting && !running ? "Отмена" : "Отключить";
            _disconnect.Visibility = running || serviceStarting || _connecting || CoreRuntime.IsRunning
                ? ViewStates.Visible
                : ViewStates.Gone;
        }

        if (_nextNodeButton is not null)
        {
            // «Сменить сервер» и «Поделиться» стоят рядом одной строкой; без подключения
            // кнопка неактивна и приглушена, чтобы не выглядела доступной.
            _nextNodeButton.Visibility = ViewStates.Visible;
            _nextNodeButton.Enabled = running && !_connecting;
            _nextNodeButton.Alpha = _nextNodeButton.Enabled ? 1f : 0.55f;
        }

        var selected = NodeStore.Selected;
        if (_shareButton is not null)
        {
            // Кнопка на месте всегда (вторая в нижнем ряду), но активна только когда
            // есть выбранный сервер: иначе делиться нечем.
            _shareButton.Visibility = ViewStates.Visible;
            _shareButton.Enabled = selected is not null;
            _shareButton.Alpha = _shareButton.Enabled ? 1f : 0.55f;
        }

        if (_addServerButton is not null)
        {
            // Ручное добавление доступно всегда, кроме момента, когда уже идёт подключение.
            _addServerButton.Visibility = ViewStates.Visible;
            _addServerButton.Enabled = !_connecting;
            _addServerButton.Alpha = _addServerButton.Enabled ? 1f : 0.55f;
        }

        if (_actionsRow2 is not null)
        {
            _actionsRow2.Visibility = ViewStates.Visible;
        }

        var rating = Rating(selected?.DelayMs);
        if (_nodeText is not null)
        {
            if (bypass)
            {
                _nodeText.Text = "Прокси-сервер не используется · стратегия TUN-обхода";
            }
            else if (!running)
            {
                // Без активного туннеля не показываем остатки прошлой сессии:
                // пинг, скорость и звёзды относились к серверу, который уже отключён.
                _nodeText.Text = connecting
                    ? "Выбираю проверенный сервер…"
                    : "Сервер не подключён — нажмите кнопку сервиса";
            }
            else if (selected is null)
            {
                _nodeText.Text = "Сервер не выбран";
            }
            else
            {
                var name = string.IsNullOrWhiteSpace(selected.Name) ? selected.Server : selected.Name;
                if (name.Length > 42)
                {
                    name = name[..41] + "…";
                }

                var endpoint = string.IsNullOrWhiteSpace(selected.Server)
                    ? string.Empty
                    : $" · {selected.Server}:{selected.Port}";
                var speed = selected.SpeedMbps is { } mbps ? $" · {mbps:0.#} Мбит/с" : string.Empty;
                _nodeText.Text = $"{name}{endpoint} · {selected.DisplayDelay}{speed} · {new string('★', rating)}{new string('☆', 5 - rating)}";
            }
        }

        if (_exitText is not null)
        {
            var show = NodeStore.Settings.ShowExitInfo;
            _exitText.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            var exit = NetworkProbe.CachedExit;
            _exitText.Text = !running
                ? "Выход: —"
                : exit is not null
                    ? "Выход: " + exit.Short
                    : _exitFailed
                        ? "Выход: не определён"
                        : "Выход: определяется…";
        }

        if (_speedText is not null)
        {
            var show = NodeStore.Settings.ShowTraffic;
            _speedText.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            var speed = running ? NetworkProbe.CachedSpeed ?? NodeStore.Selected?.SpeedMbps : null;
            _speedText.Text = speed is null ? "Скорость: —" : $"Скорость: {speed:0.#} Мбит/с";
        }

        if (_trafficText is not null)
        {
            var show = NodeStore.Settings.ShowTraffic;
            _trafficText.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            _trafficText.Text = !running || (_downBytes == 0 && _upBytes == 0)
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
                    : "Загружаю серверы…";
                _ = Task.Run(() =>
                {
                    try
                    {
                        NodeStore.EnsureLoaded();
                        RunOnUiThread(RefreshHeader);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("android", "Не удалось прочитать серверы", exception);
                        RunOnUiThread(() => _connectionStatus!.Text = "Серверы не прочитаны: " + exception.Message);
                    }
                });
                return;
            }

            try
            {
                var summary = NodeStore.Summary();
                UpdateServiceCounters(summary);

                // «Рабочих» и «универсальных» отдельно не пишем: это уже видно по иконкам сервисов.
                // Во время подключения показываем ход поиска сервера, а не сводку.
                // Фоновая проверка тоже видна: без этого казалось, что приложение молчит.
                var countText = $"В списке {summary.Total} серверов";
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
                        : "Проверить серверы";
                }
            }
            catch (Exception exception)
            {
                _connectionStatus.Text = "Серверы не прочитаны: " + exception.Message;
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
                _exitFailed = info is null;
                if (info is not null)
                {
                    RunOnUiThread(RefreshHeader);
                    TunnelService.RefreshServerInfo();
                }
            }

            // Скорость меряем независимо от страны выхода: один запрос через сервер.
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
                Ui.Toast(this, "Сначала подключитесь к серверу");
            }

            return;
        }

        if (!silent)
        {
            SetConnectionStatus("Замеряю скорость через сервер…");
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

    // ------------------------------------------------------------ поиск серверов

    /// <summary>
    /// Кнопка шапки «Проверить серверы»: проверяет ещё не проверенные серверы в процессе :probe
    /// и показывает ход прямо в шапке, не уводя на другие вкладки. Повторное нажатие
    /// во время поиска останавливает и текущую партию, и фоновый поиск.
    /// </summary>
    private async Task FindNodesAsync()
    {
        if (_finding || ProbeClient.IsRunning)
        {
            ProbeClient.Cancel();
            _headerDetail = "Поиск серверов остановлен";
            _headerDetailUntil = DateTime.UtcNow.AddSeconds(15);
            UpdateFindButton("Проверить серверы");
            RefreshHeader();
            return;
        }

        // Явное действие пользователя снимает прежнюю остановку.
        ProbeClient.Resume();
        _finding = true;
        _headerDetail = "Проверяю серверы: готовлю проверку…";
        UpdateFindButton("Остановить поиск");
        RefreshHeader();

        try
        {
            await Task.Run(() => NodeStore.EnsureLoaded());
            var progress = new Progress<ProbeProgress>(state => RunOnUiThread(() =>
            {
                _headerDetail = state.Total > 0
                    ? $"Проверяю серверы: {state.Tested} из {state.Total} · рабочих {state.Working}"
                    : "Проверяю серверы…";
                RefreshHeader();
            }));

            // recheckWorking: false — ищем именно непроверенные серверы, не тратя трафик
            // на только что проверенные рабочие.
            var summary = await ProbeClient.RunBatchAsync(
                NodeTester.MaxBatch, CancellationToken.None, progress, recheckWorking: false);

            _headerDetail = summary.Error is not null
                ? summary.Error
                : $"Проверено {summary.Tested}: рабочих {summary.Working}";
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Проверка серверов из шапки не удалась", exception);
            _headerDetail = "Проверка серверов не удалась: " + exception.Message;
        }
        finally
        {
            _finding = false;
            _headerDetailUntil = DateTime.UtcNow.AddSeconds(20);
            UpdateFindButton("Проверить серверы");
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

    /// <summary>
    /// Выбранный сервис живёт в службе: Activity пересоздаётся, а приоритет
    /// переключения должен сохраниться.
    /// </summary>
    private NodeStore.QuickService? _activeService
    {
        get => TunnelService.ActiveService;
        set => TunnelService.ActiveService = value;
    }

    private bool _finding;
    private bool _switchingNode;
    private string? _headerDetail;
    private DateTime _headerDetailUntil = DateTime.MinValue;
    private TextView? _findButton;

    private TextView? _nextNodeButton;

    /// <summary>
    /// Сколько партий по 150 серверов проверять, пока не найдётся сервер с доступом к сервису.
    /// Серверы проверяются без повторов, поэтому следующее нажатие продолжит с этого места.
    /// </summary>
    private const int FindForServiceBatches = 10;

    /// <summary>
    /// Кнопка «Дальше»: переключает на следующий проверенный сервер. Приоритет — сервис,
    /// выбранный кнопкой; внутри него — по пингу, затем остальные серверы.
    /// Если сервер уже в текущем конфиге — переключение через Clash selector, без перезапуска
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
            Ui.Toast(this, "Сначала подключитесь к серверу");
            return;
        }

        _switchingNode = true;
        try
        {
            var now = DateTime.UtcNow;
            var settings = NodeStore.Settings;
            // Снимок и сортировка тысяч серверов — в фоне: на UI-потоке это давало ANR
            // при нажатии «Дальше» (ColorOS закрывал приложение).
            // Приоритет — сервис, выбранный кнопкой: сначала его серверы (по пингу),
            // затем остальные, поэтому «Универсальный» не уйдёт на сервер без
            // универсального доступа, пока есть живые универсальные.
            var usable = await Task.Run(() => NodeFailover.Rank(
                NodeStore.Snapshot(),
                NodeStore.ServiceKey(_activeService),
                now,
                settings.ConnectOnlyMine));
            if (usable.Count == 0)
            {
                Ui.Toast(this, "Нет проверенных серверов: нажмите «Проверить серверы»");
                return;
            }

            var currentId = NodeStore.Selected?.Id;
            var currentIndex = currentId is null
                ? -1
                : usable.FindIndex(node =>
                    string.Equals(node.Id, currentId, StringComparison.OrdinalIgnoreCase));
            ProxyNode? next;
            if (currentIndex < 0)
            {
                next = usable[0];
            }
            else
            {
                // Идём по кругу от текущего: участвуют все, а свежие и лучшие —
                // первыми в своей группе приоритета.
                next = Enumerable.Range(1, usable.Count - 1)
                    .Select(step => usable[(currentIndex + step) % usable.Count])
                    .FirstOrDefault(node => !string.Equals(
                        node.Id, currentId, StringComparison.OrdinalIgnoreCase));
            }

            if (next is null)
            {
                Ui.Toast(this, "Других проверенных серверов нет");
                return;
            }

            ShowHeaderDetail("Переключаю сервер…", 6);
            if (CoreRuntime.LoadedTags.Contains(next.Tag))
            {
                using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
                await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, next.Tag, CancellationToken.None);
                NodeStore.Select(next);
                TunnelService.UpdateNodeLabel(next.DisplayDelay);
                _speedMeasured = false;
                ShowHeaderDetail("Сервер переключён · " + next.DisplayDelay, 10);
                Ui.Toast(this, "Сервер переключён · " + next.DisplayDelay);
                ScheduleExitProbe();
                return;
            }

            // Холодное переключение: сборка конфига. Если она не удалась, возвращаем
            // прежний выбор — иначе шапка показывала бы сервер, которого нет в туннеле.
            // Сортировка и сборка сотен серверов — в фоне, чтобы не блокировать UI.
            var previous = NodeStore.Selected;
            NodeStore.Select(next);
            var built = await Task.Run(() =>
            {
                var config = NodeStore.BuildTunnelConfig(out var error);
                return (Config: config, Error: error);
            });
            var config = built.Config;
            var error = built.Error;
            if (config is null)
            {
                if (previous is not null)
                {
                    NodeStore.Select(previous);
                }

                Ui.Toast(this, error ?? "Не удалось собрать конфиг");
                return;
            }

            _speedMeasured = false;
            ShowHeaderDetail("Переключаю сервер…", 6);
            StartWithConfig(config);
        }
        catch (Exception exception)
        {
            AppLog.Write("health", "Переключение на следующий сервер не удалось", exception);
            Ui.Toast(this, "Не удалось переключить сервер");
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
            // Кэш читается в фоне: на холодном старте разбор тысяч серверов на UI-потоке
            // выглядел как «приложение долго не открывается» после фоновой работы.
            await Task.Run(() => NodeStore.EnsureLoaded(), cts.Token);
            var name = ServiceName(service);
            var progress = new Progress<string>(ReportConnect);
            ReportConnect("Подключаюсь: ищу проверенный сервер…");

            // Если уже подключены через другой сервис (например, «Универсальный»),
            // нажатие «YouTube» должно именно переподключить: текущий сервер исключаем,
            // чтобы кнопка не оставила старое подключение «как есть».
            var avoidId = _activeService is { } active && active != service
                ? NodeStore.Selected?.Id
                : null;
            var node = await NodeStore.PickVerifiedAsync(service, null, progress, cts.Token, avoidId);

            if (node is null)
            {
                // Готового сервера нет: проверяем непроверенные серверы партиями, пока не найдётся
                // рабочий именно с доступом к выбранному сервису.
                node = await FindNodeForServiceAsync(service, name, cts.Token);
            }

            if (node is null)
            {
                ReportConnect(cts.IsCancellationRequested
                    ? "Поиск сервера отменён"
                    : $"Рабочий сервер для «{name}» не нашёлся");
                if (!cts.IsCancellationRequested)
                {
                    Ui.Toast(this, $"Рабочий сервер для «{name}» не нашёлся — попробуйте ещё раз позже");
                }

                return;
            }

            // Туннель уже работает и сервер загружен — переключаемся без перезапуска TUN.
            if (TunnelService.State == TunnelState.Running && CoreRuntime.IsRunning &&
                CoreRuntime.LoadedTags.Contains(node.Tag))
            {
                using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
                await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, node.Tag, cts.Token);
                TunnelService.UpdateNodeLabel(node.DisplayDelay);
                _activeService = service;
                ReportConnect($"{name}: подключено · {node.DisplayDelay}");
                Ui.Toast(this, $"{name}: подключено · {node.DisplayDelay}");
                return;
            }

            var built = await Task.Run(() =>
            {
                var tunnelConfig = NodeStore.BuildTunnelConfig(out var buildError);
                return (Config: tunnelConfig, Error: buildError);
            });
            var config = built.Config;
            if (config is null)
            {
                ReportConnect(built.Error ?? "Не удалось собрать конфиг");
                return;
            }

            _speedMeasured = false;
            _activeService = service;
            ReportConnect($"{name}: живой сервер · {node.DisplayDelay}");
            StartWithConfig(config);
        }
        catch (System.OperationCanceledException)
        {
            ReportConnect("Поиск сервера отменён");
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
    /// Проверяет партии непроверенных серверов, пока не появится рабочий сервер с доступом
    /// к сервису. Уже проверенные серверы не повторяются, поэтому список продвигается вперёд.
    /// </summary>
    private async Task<ProxyNode?> FindNodeForServiceAsync(
        NodeStore.QuickService service,
        string name,
        CancellationToken cancellationToken)
    {
        var batches = 0;
        // Не держим кнопку «зависшей»: ищем не дольше 90 секунд, дальше поиск
        // продолжается в фоне, а пользователь получает понятный ответ.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        for (var attempt = 0; batches < FindForServiceBatches && DateTime.UtcNow < deadline; attempt++)
        {
            if (attempt > FindForServiceBatches * 12)
            {
                return null;
            }

            // Фоновая партия уже идёт: не запускаем вторую, а ждём её результаты —
            // они появятся в NodeStore и могут сразу дать сервер для сервиса.
            if (ProbeClient.IsRunning)
            {
                var ready = NodeStore.PickForService(service);
                if (ready is not null)
                {
                    return ready;
                }

                ReportConnect($"Жду фоновую проверку серверов — «{name}»…");
                await Task.Delay(1500, cancellationToken);
                continue;
            }

            ReportConnect($"Нет готового сервера для «{name}»: проверяю серверы, партия {batches + 1} из {FindForServiceBatches}…");
            var progress = new Progress<ProbeProgress>(state =>
                ReportConnect($"Ищу сервер для «{name}» — {state.Describe()}"));
            var batchStarted = DateTime.UtcNow;
            var batch = await ProbeClient.RunBatchAsync(
                NodeTester.MaxBatch, cancellationToken, progress, recheckWorking: false);

            if (batch.Error == ProbeClient.BusyError)
            {
                // Параллельная проверка (фоновая) — подождём её и продолжим.
                await Task.Delay(1500, cancellationToken);
                continue;
            }

            if (batch.Tested == 0)
            {
                ReportConnect(batch.Error ?? "Непроверенных серверов не осталось");
                return null;
            }

            batches++;
            // Только сервер, проверенный в этой партии: прежний «рабочий» мог уже умереть.
            var found = ProbeClient.PickFreshForService(service, batchStarted);
            if (found is not null)
            {
                return found;
            }

            ReportConnect($"{name}: в партии {batches} подходящих серверов нет, проверяю дальше…");
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
        _exitFailed = false;
        _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            await EnsureExitAsync();
        });
    }

    /// <summary>
    /// Сервер вручную: ссылка с ключом (vless://…) или IP:порт сервера, который уже есть
    /// в списках. Добавленный сервер проверяется вне общей очереди и сразу подключается.
    /// </summary>
    private void ShowAddServerDialog()
    {
        var input = new EditText(this)
        {
            Hint = "vless://… или 1.2.3.4:443",
        };
        input.SetSingleLine(true);
        input.SetTextColor(P.TextSoft());
        input.SetHintTextColor(P.Muted);
        var dialog = new AlertDialog.Builder(this)
            .SetTitle("Добавить сервер")
            .SetMessage("Вставьте ссылку с ключом (vless://, vmess://, trojan://, ss://, hysteria2://) " +
                        "или IP:порт сервера, который уже есть в списках.")
            .SetView(input)
            .SetPositiveButton("Добавить и проверить", (_, _) => _ = AddServerAndConnectAsync(input.Text))
            .SetNegativeButton("Отмена", (_, _) => { })
            .Create();
        dialog.Show();
    }

    private async Task AddServerAndConnectAsync(string? text)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return;
        }

        if (_connecting)
        {
            Ui.Toast(this, "Подключение уже идёт");
            return;
        }

        _connecting = true;
        var cts = new CancellationTokenSource();
        _connectCts = cts;
        try
        {
            await Task.Run(() => NodeStore.EnsureLoaded(), cts.Token);
            List<ProxyNode> candidates;
            if (raw.Contains("://", StringComparison.Ordinal))
            {
                var node = LinkParser.Parse(raw, "вручную")
                           ?? SubscriptionParser.Parse(raw, "вручную").FirstOrDefault();
                if (node is null)
                {
                    Ui.Toast(this, "Ссылка не разобралась: нужна ссылка на один сервер с ключом");
                    return;
                }

                NodeStore.AddManual(node);
                candidates = new List<ProxyNode> { node };
            }
            else
            {
                var host = raw;
                var port = 0;
                var colon = raw.LastIndexOf(':');
                if (colon > 0 && int.TryParse(raw[(colon + 1)..], out var parsedPort))
                {
                    host = raw[..colon].Trim();
                    port = parsedPort;
                }

                candidates = NodeStore.Snapshot()
                    .Where(node => node.Status != NodeStatus.Dead &&
                                   node.Server.Equals(host, StringComparison.OrdinalIgnoreCase) &&
                                   (port == 0 || node.Port == port))
                    .Take(6)
                    .ToList();
                if (candidates.Count == 0)
                {
                    Ui.Toast(this,
                        $"Сервер {host}{(port > 0 ? ":" + port : string.Empty)} не найден в списках. " +
                        "Добавьте ссылку с ключом (vless://…).");
                    return;
                }
            }

            ReportConnect("Проверяю добавленный сервер…");
            var progress = new Progress<ProbeProgress>(state => ReportConnect(state.Describe()));
            var summary = await ProbeClient.RunAsync(candidates, cts.Token, progress);
            if (summary.Error is not null && summary.Tested == 0)
            {
                ReportConnect(summary.Error);
                return;
            }

            var now = DateTime.UtcNow;
            var best = candidates
                .Where(node => NodeAvailability.IsUsable(
                    node, NodeLimits.WorkingRecheckMinutes, NodeStore.Settings.ConnectOnlyMine, now))
                .OrderBy(node => node.DelayMs ?? int.MaxValue)
                .ThenByDescending(node => node.OpenDomains)
                .FirstOrDefault();
            if (best is null)
            {
                ReportConnect("Сервер не ответил");
                Ui.Toast(this, "Добавленный сервер не открыл проверочные сайты");
                return;
            }

            NodeStore.Select(best);
            var built = await Task.Run(() =>
            {
                var config = NodeStore.BuildTunnelConfig(out var error);
                return (Config: config, Error: error);
            });
            if (built.Config is null)
            {
                ReportConnect(built.Error ?? "Не удалось собрать конфиг");
                return;
            }

            _speedMeasured = false;
            _activeService = null;
            ReportConnect($"{best.Server}:{best.Port} · {best.DisplayDelay}");
            Ui.Toast(this, $"Сервер добавлен · {best.DisplayDelay}");
            StartWithConfig(built.Config);
        }
        catch (System.OperationCanceledException)
        {
            ReportConnect("Проверка отменена");
        }
        catch (Exception exception)
        {
            AppLog.Write("android", "Добавление сервера не удалось", exception);
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
            ScheduleHeaderRefresh();
        }
    }

    /// <summary>Поделиться ссылкой подключённого (или выбранного) сервера.</summary>
    private void ShareCurrentNode()
    {
        var node = NodeStore.Selected;
        if (node is null)
        {
            Ui.Toast(this, "Нет сервера для отправки");
            return;
        }

        // Владелец просил делиться только адресом сервера, без длинной ссылки
        // с параметрами и названием: IP (или домен) и порт.
        var endpoint = string.IsNullOrWhiteSpace(node.Server)
            ? node.Name
            : $"{node.Server}:{node.Port}";
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Ui.Toast(this, "Не удалось определить адрес сервера");
            return;
        }

        try
        {
            var send = new Intent(Intent.ActionSend)
                .SetType("text/plain")
                .PutExtra(Intent.ExtraSubject, "VieNLink — сервер")
                .PutExtra(Intent.ExtraText, endpoint);
            var handlers = PackageManager?.QueryIntentActivities(send, 0)?.Count ?? 0;
            AppLog.Write("android", $"Поделиться сервером: обработчиков {handlers}");
            if (handlers > 0)
            {
                var chooser = Intent.CreateChooser(send, "Поделиться сервером");
                if (chooser is not null)
                {
                    StartActivity(chooser);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("android", "Не удалось открыть «Поделиться»", exception);
        }

        // Запасной путь: буфер обмена, если на устройстве нет обработчика Intent.Send.
        try
        {
            var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
            if (clipboard is not null)
            {
                clipboard.PrimaryClip = ClipData.NewPlainText("VieNLink", endpoint);
                Ui.Toast(this, "Адрес сервера скопирован");
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("android", "Не удалось скопировать адрес сервера", exception);
            Ui.Toast(this, "Не удалось поделиться сервером");
        }
    }

    private void StopEverything()
    {
        _activeService = null;
        // Долгий поиск сервера тоже прерываем: иначе он продолжился бы после «Отключить».
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
        // поэтому такая ссылка сразу становится источником и попадает в список серверов.
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
