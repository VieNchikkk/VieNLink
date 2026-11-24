using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Главный экран Android-версии: шапка с состоянием, четыре нижние вкладки
/// (Подключение / Обход / Инструменты / Настройки) и быстрое подключение.
/// Сервер всегда выбирается автоматически из уже проверенных.
/// </summary>
[Activity(Label = BuildInfo.AppLabel, MainLauncher = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTop,
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation |
                           Android.Content.PM.ConfigChanges.ScreenSize |
                           Android.Content.PM.ConfigChanges.SmallestScreenSize |
                           Android.Content.PM.ConfigChanges.FontScale |
                           Android.Content.PM.ConfigChanges.UiMode,
    Theme = "@style/Theme.VieNLink")]
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
    /// <summary>Планшет/ТВ: левая колонка с шапкой; на телефоне null — шапка в странице.</summary>
    private LinearLayout? _headerHost;
    private LinearLayout? _nav;
    private readonly List<ImageView> _navIcons = new();
    private readonly List<TextView> _navLabels = new();
    private int _tab;
    private bool _openingSettings;

    private TextView? _statusText;
    private TextView? _nodeText;
    private TextView? _nodeSubText;
    private TextView? _delayText;
    private TextView? _exitText;
    private TextView? _trafficText;
    private TextView? _stateDot;
    private ImageView? _disconnect;
    private TextView? _youtubeCount;
    private TextView? _workingCount;
    private TextView? _workingDelta;

    /// <summary>Сколько рабочих было перед текущим (или последним) прогоном.</summary>
    private int _workingBaseline = -1;

    /// <summary>Последнее изменение числа рабочих: одна цифра рядом со значком.</summary>
    private int _workingChange;
    private int _lastWorkingSeen = -1;
    private TextView? _discordCount;
    private TextView? _telegramCount;
    private TextView? _universalCount;
    /// <summary>Блок «Сервер открывает»: значки сервисов у выбранного (подключённого) сервера.</summary>
    private LinearLayout? _serverServicesRow;
    private LinearLayout? _serverServicesBadges;
    private string _serverServicesSignature = string.Empty;
    private TextView? _connectionStatus;
    private TextView? _probeBadge;
    private TextView? _shareButton;
    private TextView? _addServerButton;
    private LinearLayout? _actionsRow2;

    private Handler? _handler;
    private Action? _tick;
    private CancellationTokenSource? _resumeCountersCts;
    private string? _probeBadgeText;
    private Color? _probeBadgeTone;
    private Color? _connectionStatusTone;
    private DateTime _lastExitAttempt = DateTime.MinValue;
    private bool _exitFailed;
    private bool _exitBusy;
    private bool _speedMeasured;
    private string? _pendingConfig;
    private CancellationTokenSource? _trafficCts;
    private long _downBytes;
    private long _upBytes;
    private DateTime _lastTrafficUi = DateTime.MinValue;

    private static readonly string[] TabTitles = { "Подключение", "Обход", "Инструменты", "Настройки" };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        BuildShell();

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

        if (DeviceProfile.IsTelevision)
        {
            // Пульт: сразу ставим фокус на главное действие, а не «в никуда».
            new Handler(Looper.MainLooper!).Post(() => _findButton?.RequestFocus());
        }

        if (Intent?.GetBooleanExtra("nextNode", false) == true)
        {
            _ = SwitchToNextNodeAsync();
        }

        // Автопроверка обновлений при запуске: релиз с более высокой версией
        // скачивается и предлагается к установке.
        UpdateCenter.StartLaunchCheck(this, Intent?.GetBooleanExtra("checkUpdate", false) == true);
    }

    /// <summary>
    /// Корневая раскладка: на телефоне одна колонка, на планшете и ТВ — шапка слева,
    /// страница справа. Строится заново при повороте и изменении размера окна,
    /// поэтому отступы и колонки всегда соответствуют текущему экрану.
    /// </summary>
    private void BuildShell()
    {
        _navIcons.Clear();
        _navLabels.Clear();
        _headerHost = null;

        var shell = Ui.Column(this);
        shell.SetBackgroundColor(P.Background);

        if (DeviceProfile.IsLargeScreen)
        {
            // Планшет/ТВ: шапка и действия слева, содержимое вкладки справа.
            // Каждая колонка прокручивается независимо; внизу — общая навигация.
            var row = Ui.Row(this);
            row.SetGravity(GravityFlags.Top);

            var left = new ScrollView(this) { FillViewport = true, VerticalScrollBarEnabled = false };
            _headerHost = Ui.Column(this);
            _headerHost.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 8), Ui.Dp(this, 8), Ui.Dp(this, 16));
            left.AddView(_headerHost, new ScrollView.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            var right = new ScrollView(this) { FillViewport = true, VerticalScrollBarEnabled = false };
            _pageHost = Ui.Column(this);
            _pageHost.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 8), Ui.Dp(this, 16), Ui.Dp(this, 16));
            right.AddView(_pageHost, new ScrollView.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            row.AddView(left, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 0.44f));
            row.AddView(right, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 0.56f));
            shell.AddView(row, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));
            // Строка состояния и вырез — сверху над обеими колонками; низ закрывает навигация.
            Ui.ApplyInsets(row, 0, 0, 0, 0, insetBottom: false);
        }
        else
        {
            var scroll = new ScrollView(this) { FillViewport = true, VerticalScrollBarEnabled = false };
            _pageHost = Ui.Column(this);
            _pageHost.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 8), Ui.Dp(this, 16), Ui.Dp(this, 16));
            scroll.AddView(_pageHost, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            shell.AddView(scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f));
            Ui.ApplyInsets(scroll, 0, 0, 0, 0, insetBottom: false);
        }

        _nav = BuildNavigation();
        shell.AddView(_nav, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        SetContentView(shell);
    }

    /// <summary>Поворот, изменение размера окна, смена системного шрифта.</summary>
    protected override void OnLayoutConfigurationChanged()
    {
        BuildShell();
        SelectTab(_tab);
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

        if (intent?.GetBooleanExtra("nextNode", false) == true)
        {
            _ = SwitchToNextNodeAsync();
        }

        RefreshHeader();
    }

    protected override void OnResume()
    {
        base.OnResume();
        // Настройки открываются отдельным экраном с четвёртой вкладки; после возврата
        // снимаем защиту и показываем прежнюю вкладку. В остальных случаях страницу
        // не пересобираем: при возврате из фона SelectTab заново строил весь экран
        // (шапку и список), и приложение заметно зависало на несколько секунд.
        var returningFromSettings = _openingSettings;
        _openingSettings = false;
        if (returningFromSettings)
        {
            SelectTab(_tab);
        }

        RefreshHeader();
        EnsureTrafficMonitor();

        // Счётчики после возврата из фона обновляем в фоне и не в первом кадре.
        // Раньше работа стартовала прямо из OnResume: снимок тысяч серверов и запуск
        // :probe совпадали с отрисовкой окна. При редком дедлоке остановки мира
        // в Mono это давало пустой экран и ANR «Broadcast of Intent … MAINTENANCE».
        _resumeCountersCts?.Cancel();
        var countersCts = new CancellationTokenSource();
        _resumeCountersCts = countersCts;
        _ = Task.Run(() => EnsureFreshCountersAsync(countersCts.Token));

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
    /// перепроверки: проверяем ранее рабочие серверы одной небольшой партией.
    /// Всё идёт в фоне и с задержкой — окно должно успеть нарисоваться: снимок списка
    /// серверов и запуск :probe на первом кадре давали зависание и ANR.
    /// </summary>
    private async Task EnsureFreshCountersAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(2500, token).ConfigureAwait(false);

            if (SettingsStore.Current.AutomationPaused || ProbeClient.IsPaused ||
                ProbeClient.IsRunning || ProbeClient.IsManualRun)
            {
                return;
            }

            await Task.Run(() => NodeStore.EnsureLoaded(), token).ConfigureAwait(false);

            // Сначала стартовая перепроверка всех рабочих (если ещё не прошла): она и
            // обновляет счётчики. Быстрый проход ниже — только если рабочие устарели уже после неё.
            await ProbeScheduler.EnsureStartupRecheckAsync(token).ConfigureAwait(false);

            if (NodeStore.Summary().Working >= ProbeProfile.MinWorking)
            {
                return;
            }

            // workingOnly: только серверы, которые уже были рабочими и чья проверка истекла.
            var summary = await ProbeClient
                .RunBatchAsync(20, token, workingOnly: true)
                .ConfigureAwait(false);
            if (summary.Tested > 0 && !token.IsCancellationRequested)
            {
                RunOnUiThread(RefreshHeader);
            }
        }
        catch (System.OperationCanceledException)
        {
            // Вернулись в фон до конца обновления — ничего страшного.
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Не удалось быстро обновить счётчики при возврате", exception);
        }
    }

    protected override void OnPause()
    {
        StopTrafficMonitor();

        // Обновление счётчиков, запущенное при возврате, уже не нужно: партия
        // не должна занимать :probe, пока приложение в фоне.
        _resumeCountersCts?.Cancel();
        _resumeCountersCts?.Dispose();
        _resumeCountersCts = null;

        // Дописываем кэш при уходе из приложения: рабочий файл маленький,
        // а куча непроверенных могла ждать своей очереди.
        _ = Task.Run(() => NodeStore.SaveNodes(force: true));

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
        // Нижний отступ — по реальному инсету панели навигации (кнопочной или жестовой).
        Ui.ApplyInsets(nav, 6, 6, 6, 6, insetTop: false);
        nav.SetBackgroundColor(P.Panel);

        var icons = new[]
        {
            Resource.Drawable.ic_tab_connection,
            Resource.Drawable.ic_tab_bypass,
            Resource.Drawable.ic_tab_tools,
            Resource.Drawable.ic_settings,
        };

        for (var index = 0; index < TabTitles.Length; index++)
        {
            var item = Ui.Column(this);
            item.SetGravity(GravityFlags.Center);
            item.SetPadding(Ui.Dp(this, 2), Ui.Dp(this, Ui.IsShortLayout ? 4 : 7),
                Ui.Dp(this, 2), Ui.Dp(this, 6));
            item.Clickable = true;
            item.Focusable = true;

            var position = index;
            item.Click += (_, _) => SelectTab(position);
            if (Ui.IsTelevision)
            {
                // Пульт: видно, на какой вкладке фокус.
                item.FocusChange += (_, e) =>
                {
                    item.Foreground = e.HasFocus
                        ? Ui.Rounded(Color.Transparent, 12, this, P.Accent, 2.2f)
                        : null;
                    item.ScaleX = e.HasFocus ? 1.08f : 1f;
                    item.ScaleY = e.HasFocus ? 1.08f : 1f;
                };
            }

            var image = new ImageView(this);
            var drawable = GetDrawable(icons[index])?.Mutate();
            if (drawable is not null)
            {
                image.SetImageDrawable(drawable);
            }

            var iconSize = Ui.Dp(this, Ui.IsCompactLayout ? 20 : 22);
            item.AddView(image, new LinearLayout.LayoutParams(iconSize, iconSize));
            var label = Ui.Text(this, TabTitles[index], Ui.IsCompactLayout ? 10f : 10.5f, P.Muted, bold: true);
            label.Gravity = GravityFlags.Center;
            label.SetSingleLine(true);
            label.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
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
        // Четвёртая вкладка — «Настройки»: отдельный экран, как и раньше,
        // но теперь на месте журнала. Журнал переехал в «Инструменты».
        if (index >= TabTitles.Length - 1)
        {
            // Настройки — отдельный экран. Возврат должен показывать прежнюю вкладку,
            // а не открывать их снова; если пришли сразу на «Настройки» (intent tab=3),
            // откатываемся на «Подключение».
            if (_tab >= TabTitles.Length - 1)
            {
                _tab = 0;
            }

            if (_openingSettings)
            {
                return;
            }

            _openingSettings = true;
            Open(typeof(SettingsActivity));
            return;
        }

        _tab = Math.Clamp(index, 0, TabTitles.Length - 1);
        if (_pageHost is null)
        {
            return;
        }

        var header = BuildHeader();
        View page = _tab switch
        {
            0 => BuildConnectionPage(),
            1 => BuildBypassPage(),
            _ => BuildToolsPage(),
        };

        if (_headerHost is not null)
        {
            // Планшет/ТВ: шапка слева, страница справа.
            _headerHost.RemoveAllViews();
            _headerHost.AddView(header, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            _pageHost.RemoveAllViews();
            _pageHost.AddView(page, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }
        else
        {
            _pageHost.RemoveAllViews();
            _pageHost.AddView(header, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            Ui.Add(_pageHost, page, 12);
        }

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
        // Шапку пересобрали: кэш прошлых текстов/цветов не должен скрыть обновления.
        _probeBadgeText = null;
        _probeBadgeTone = null;
        _connectionStatusTone = null;
        // Значки сервисов пересоздаются пустыми: без сброса подписи обновление
        // посчитало бы их уже нарисованными и оставило строку пустой.
        _serverServicesSignature = string.Empty;

        var card = Ui.HeaderCard(P, this);
        // Логотип и название чуть выше: меньше верхнего отступа карточки.
        card.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 8), Ui.Dp(this, 16), Ui.Dp(this, 14));

        var top = Ui.Row(this);
        var logo = new ImageView(this);
        logo.SetScaleType(ImageView.ScaleType.CenterCrop);
        logo.SetImageResource(Resource.Mipmap.ic_launcher);
        logo.Background = Ui.Rounded(P.Inner, 12, this, P.Border);
        logo.ClipToOutline = true;
        var logoSide = Ui.Dp(this, Ui.IsCompactLayout ? 40 : 46);
        top.AddView(logo, new LinearLayout.LayoutParams(logoSide, logoSide));

        // Название, метка «БЕТА» и номер версии. Название ужимается первым
        // (одна строка с многоточием), метка и версия не сжимаются никогда:
        // раньше при крупном системном шрифте версия переносилась по буквам.
        var titles = Ui.Row(this);
        titles.SetPadding(Ui.Dp(this, 12), 0, 0, 0);
        var nameText = Ui.Text(this, "VieNLink", 21f, P.TextStrong, bold: true);
        nameText.SetSingleLine(true);
        nameText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        titles.AddView(nameText, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));
        var betaBadge = Ui.Badge(this, P, "БЕТА", P.Warning);
        var betaLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        betaLayout.LeftMargin = Ui.Dp(this, 8);
        titles.AddView(betaBadge, betaLayout);
        top.AddView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var versionText = Ui.Text(this, "v" + UpdateCenter.CurrentVersion(this), 12f, P.Muted, bold: true);
        versionText.SetSingleLine(true);
        versionText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        versionText.SetPadding(Ui.Dp(this, 8), 0, 0, 0);
        top.AddView(versionText, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        card.AddView(top);

        var statusRow = Ui.Row(this);
        statusRow.SetPadding(0, 0, 0, Ui.Dp(this, 8));
        // Постоянная высота строки статуса: кнопки появляются, но текст ниже не съезжает.
        statusRow.SetMinimumHeight(Ui.Dp(this, 44));
        _stateDot = Ui.Text(this, "●", 15f, P.Muted);
        statusRow.AddView(_stateDot);
        _statusText = Ui.Text(this, "VPN отключён", 13f, P.TextStrong, bold: true);
        _statusText.SetPadding(Ui.Dp(this, 7), 0, 0, 0);
        // Подпись статуса занимает всё свободное место и обрезается многоточием:
        // кнопки справа всегда прижаты к краю и никогда не выдавливаются за карточку
        // (раньше при крупном шрифте значок «выключить» обрезался).
        _statusText.SetSingleLine(true);
        _statusText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        statusRow.AddView(_statusText, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));

        // «Сменить сервер» — компактная кнопка: мелкая надпись «сменить» и стрелка «далее».
        _nextNodeButton = Ui.Column(this);
        _nextNodeButton.SetPadding(Ui.Dp(this, 4), Ui.Dp(this, 2), Ui.Dp(this, 4), Ui.Dp(this, 2));
        _nextNodeButton.SetGravity(GravityFlags.Center);
        _nextNodeButton.Background = Ui.Pressable(this,
            Ui.Blend(P.Card, P.Telegram, 0.20f), Ui.Blend(P.Card, P.Telegram, 0.30f), 12,
            Color.Argb(150, P.Telegram.R, P.Telegram.G, P.Telegram.B));
        _nextNodeButton.Clickable = true;
        _nextNodeButton.Focusable = true;
        _nextNodeButton.Click += (_, _) => _ = SwitchToNextNodeAsync();
        _nextNodeButton.ContentDescription = "Сменить сервер";
        _nextNodeButton.Visibility = ViewStates.Visible;
        _nextNodeButton.Enabled = false;
        _nextNodeButton.Alpha = 0.4f;

        var changeLabel = Ui.Text(this, "сменить", 8f, P.Telegram, bold: true);
        changeLabel.Gravity = GravityFlags.CenterHorizontal;
        changeLabel.SetSingleLine(true);
        _nextNodeButton.AddView(changeLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var nextIconView = new ImageView(this);
        var nextIcon = Ui.Icon(this, Resource.Drawable.ic_next);
        if (nextIcon is not null)
        {
            nextIcon.SetTint(P.Telegram);
            nextIconView.SetImageDrawable(nextIcon);
        }
        var nextIconLayout = new LinearLayout.LayoutParams(Ui.Dp(this, 18), Ui.Dp(this, 18));
        nextIconLayout.Gravity = GravityFlags.CenterHorizontal;
        _nextNodeButton.AddView(nextIconView, nextIconLayout);

        var headerButtonWidth = Ui.Dp(this, Ui.IsCompactLayout ? 46 : 52);
        var headerButtonHeight = Ui.Dp(this, 40);
        var nextNodeLayout = new LinearLayout.LayoutParams(headerButtonWidth, headerButtonHeight);
        nextNodeLayout.LeftMargin = Ui.Dp(this, 8);
        nextNodeLayout.RightMargin = Ui.Dp(this, 6);
        statusRow.AddView(_nextNodeButton, nextNodeLayout);

        // «Отключить/Отмена» — только значок питания, без подписи.
        _disconnect = new ImageView(this);
        var powerIcon = Ui.Icon(this, Resource.Drawable.ic_power);
        if (powerIcon is not null)
        {
            powerIcon.SetTint(P.Danger);
            _disconnect.SetImageDrawable(powerIcon);
        }
        _disconnect.SetPadding(Ui.Dp(this, 10), Ui.Dp(this, 10), Ui.Dp(this, 10), Ui.Dp(this, 10));
        _disconnect.Background = Ui.Pressable(this,
            Ui.Blend(P.Card, P.Danger, 0.18f), Ui.Blend(P.Card, P.Danger, 0.30f), 12,
            Color.Argb(150, P.Danger.R, P.Danger.G, P.Danger.B));
        _disconnect.Clickable = true;
        _disconnect.Focusable = true;
        _disconnect.Click += (_, _) => StopEverything();
        _disconnect.ContentDescription = "Отключить";
        _disconnect.Visibility = ViewStates.Visible;
        _disconnect.Enabled = false;
        _disconnect.Alpha = 0.4f;
        statusRow.AddView(_disconnect, new LinearLayout.LayoutParams(headerButtonWidth, headerButtonHeight));
        // Строка статуса добавляется внутрь блока информации о сервере (ниже).

        // Информация о сервере — отдельный блок с рамкой и стабильными строками,
        // чтобы при подключении ничего не прыгало и не переносилось криво.
        var nodeCard = Ui.Column(this);
        nodeCard.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 2), Ui.Dp(this, 12), Ui.Dp(this, 9));
        nodeCard.Background = Ui.Rounded(P.Inner, 12, this, P.Border);
        var nodeCardLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        nodeCardLayout.TopMargin = Ui.Dp(this, 6);

        // Строка статуса и кнопки «сменить/выключить» — сверху внутри блока.
        nodeCard.AddView(statusRow);

        var nodeNameRow = Ui.Row(this);
        _nodeText = Ui.Text(this, "Сервер не подключён", 14f, P.TextStrong, bold: true);
        _nodeText.SetSingleLine(true);
        _nodeText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        // Ширина имени — доля экрана с пределом: на узком экране имя не выдавливает
        // задержку за край карточки.
        _nodeText.SetMaxWidth(Ui.Share(this, 0.55, 200));
        nodeNameRow.AddView(_nodeText, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        // Пинг — сразу за названием, а не у правого края.
        _delayText = Ui.Text(this, string.Empty, 12.5f, P.Positive, bold: true);
        _delayText.SetSingleLine(true);
        _delayText.SetPadding(Ui.Dp(this, 8), 0, 0, 0);
        nodeNameRow.AddView(_delayText);
        nodeCard.AddView(nodeNameRow);

        _nodeSubText = Ui.Text(this, "Нажмите кнопку сервиса", 11.5f, P.MutedStrong);
        _nodeSubText.SetSingleLine(true);
        _nodeSubText.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
        _nodeSubText.SetPadding(0, Ui.Dp(this, 3), 0, 0);
        nodeCard.AddView(_nodeSubText);

        // «Выход» и «Трафик» — в одной строке: две отдельные строки съедали
        // высоту шапки, и на низких экранах кнопки не помещались целиком.
        var infoRow = Ui.Row(this);
        infoRow.SetPadding(0, Ui.Dp(this, 3), 0, 0);
        _exitText = Ui.Text(this, "Выход: —", 11.5f, P.Accent);
        _exitText.SetSingleLine(true);
        _exitText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        infoRow.AddView(_exitText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _trafficText = Ui.Text(this, "Трафик: —", 11.5f, P.Positive);
        _trafficText.SetSingleLine(true);
        _trafficText.Gravity = GravityFlags.Right;
        _trafficText.SetPadding(Ui.Dp(this, 8), 0, 0, 0);
        infoRow.AddView(_trafficText, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        nodeCard.AddView(infoRow);

        card.AddView(nodeCard, nodeCardLayout);

        // Что открывает именно выбранный (подключённый) сервер — как значки в отсеве.
        // Сюда попадают только сервисы этого сервера, в отличие от счётчиков ниже,
        // которые показывают, сколько серверов вообще готовы к каждому сервису.
        // Подпись и значки — в одной строке (раньше это были две строки).
        _serverServicesRow = Ui.Row(this);
        _serverServicesRow.SetPadding(0, Ui.Dp(this, 8), 0, 0);
        var servicesLabel = Ui.Text(this, "СЕРВЕР ОТКРЫВАЕТ", 10.5f, P.Positive, bold: true);
        servicesLabel.LetterSpacing = 0.06f;
        servicesLabel.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 3), Ui.Dp(this, 8), Ui.Dp(this, 3));
        servicesLabel.Background = Ui.Rounded(Color.Argb(36, P.Positive.R, P.Positive.G, P.Positive.B), 8, this,
            Color.Argb(110, P.Positive.R, P.Positive.G, P.Positive.B));
        _serverServicesRow.AddView(servicesLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        _serverServicesBadges = Ui.Row(this);
        _serverServicesBadges.SetPadding(Ui.Dp(this, 6), 0, 0, 0);
        // Горизонтальная прокрутка: четыре значка сервисов на узком экране
        // не помещаются и раньше обрезались справа.
        var servicesScroll = new HorizontalScrollView(this)
        {
            HorizontalScrollBarEnabled = false,
        };
        servicesScroll.AddView(_serverServicesBadges, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        _serverServicesRow.AddView(servicesScroll, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));
        _serverServicesRow.Visibility = ViewStates.Visible;
        nodeCard.AddView(_serverServicesRow);

        // Одна общая рамка: слева «Быстрое подключение», справа — рабочие серверы
        // (щит, число, дельта, подпись) без отдельных вложенных плашек.
        var servicesHeaderRow = Ui.Row(this);
        servicesHeaderRow.SetPadding(Ui.Dp(this, 10), Ui.Dp(this, 3), Ui.Dp(this, 10), Ui.Dp(this, 3));
        servicesHeaderRow.Background = Ui.Rounded(Color.Argb(36, P.Accent.R, P.Accent.G, P.Accent.B), 10, this,
            Color.Argb(110, P.Accent.R, P.Accent.G, P.Accent.B));
        var servicesHeaderLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        servicesHeaderLayout.TopMargin = Ui.Dp(this, 8);

        var servicesTitle = Ui.Text(this, "БЫСТРОЕ ПОДКЛЮЧЕНИЕ", 9f, P.Accent, bold: true);
        servicesTitle.LetterSpacing = 0f;
        servicesTitle.SetSingleLine(true);
        servicesTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        // Заголовок занимает свободное место: счётчик и подпись справа не сжимаются
        // и не переносятся на узком экране.
        servicesHeaderRow.AddView(servicesTitle, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));

        var shield = new ImageView(this);
        var shieldDrawable = Ui.Icon(this, Resource.Drawable.ic_nodes);
        if (shieldDrawable is not null)
        {
            shieldDrawable.SetTint(P.Positive);
            shield.SetImageDrawable(shieldDrawable);
        }

        servicesHeaderRow.AddView(shield,
            new LinearLayout.LayoutParams(Ui.Dp(this, 15), Ui.Dp(this, 15)));
        _workingCount = Ui.Text(this, "—", 12f, P.Positive, bold: true);
        _workingCount.SetPadding(Ui.Dp(this, 5), 0, 0, 0);
        servicesHeaderRow.AddView(_workingCount, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        // Подпись «рабочих серверов», затем дельта — вплотную за подписью (не у края).
        // На узком экране подпись короче: иначе она выдавливала заголовок.
        var workingLabel = Ui.Text(this, Ui.IsCompactLayout ? "рабочих" : "рабочих серверов",
            10f, P.Positive, bold: true);
        workingLabel.SetSingleLine(true);
        workingLabel.SetPadding(Ui.Dp(this, 5), 0, 0, 0);
        servicesHeaderRow.AddView(workingLabel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        _workingDelta = Ui.Text(this, string.Empty, 11.5f, P.Muted, bold: true);
        _workingDelta.SetPadding(Ui.Dp(this, 4), 0, 0, 0);
        _workingDelta.SetMinWidth(Ui.Dp(this, Ui.IsCompactLayout ? 18 : 24));
        _workingDelta.Gravity = GravityFlags.Left;
        _workingDelta.Visibility = ViewStates.Invisible;
        servicesHeaderRow.AddView(_workingDelta, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        card.AddView(servicesHeaderRow, servicesHeaderLayout);
        var tv = DeviceProfile.IsTelevision;
        var serviceRow1 = Ui.Row(this);
        serviceRow1.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        AddWeighted(serviceRow1, ServiceHeaderButton("YouTube", Resource.Drawable.ic_service_youtube_brand,
            NodeStore.QuickService.YouTube, P.YouTube, P.YouTubeSurface, out _youtubeCount, tintIcon: false),
            tv ? 0 : 8);
        if (!tv)
        {
            AddWeighted(serviceRow1, ServiceHeaderButton("Discord", Resource.Drawable.ic_service_discord,
                NodeStore.QuickService.Discord, P.Discord, P.DiscordSurface, out _discordCount), 0);
        }
        card.AddView(serviceRow1);

        var serviceRow2 = Ui.Row(this);
        var serviceRow2Layout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        serviceRow2Layout.TopMargin = Ui.Dp(this, 6);
        AddWeighted(serviceRow2, ServiceHeaderButton(
            tv ? "Zona" : Ui.IsCompactLayout ? "Универс." : "Универсальный",
            tv ? Resource.Drawable.ic_service_zona : Resource.Drawable.ic_service_universal,
            NodeStore.QuickService.Universal, P.Universal, P.UniversalSurface, out _universalCount),
            tv ? 0 : 8);
        if (!tv)
        {
            AddWeighted(serviceRow2, ServiceHeaderButton("Telegram", Resource.Drawable.ic_service_telegram,
                NodeStore.QuickService.Telegram, P.Telegram, P.TelegramSurface, out _telegramCount), 0);
        }
        card.AddView(serviceRow2, serviceRow2Layout);

        // Служебные кнопки — одной строкой: MTProto-прокси и «Добавить».
        // Раньше это были две отдельные строки на всю ширину, и шапка не
        // помещалась на низких экранах (640dp и меньше).
        var utilityRow = Ui.Row(this);
        var utilityLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        utilityLayout.TopMargin = Ui.Dp(this, 8);
        if (!tv)
        {
            var mtproto = Ui.Pill(this, P, DeviceProfile.IsTablet ? "MTProto-прокси" : "MTProto",
                UiButtonKind.Secondary, () => Open(typeof(TelegramActivity)),
                Resource.Drawable.ic_mtproto, tone: P.Telegram);
            CompactHeaderUtilityButton(mtproto);
            StyleHeaderAction(mtproto, P.Telegram, filled: false);
            utilityRow.AddView(mtproto, new LinearLayout.LayoutParams(
                0, ViewGroup.LayoutParams.WrapContent, 1f));
            utilityRow.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        }

        _addServerButton = Ui.Pill(this, P,
            tv ? "Добавить сервер" : Ui.IsCompactLayout ? "Добавить" : "Добавить сервер",
            UiButtonKind.Accent, ShowAddServerDialog, Resource.Drawable.ic_add);
        CompactHeaderUtilityButton(_addServerButton);
        StyleHeaderAction(_addServerButton, P.Positive, filled: false);
        utilityRow.AddView(_addServerButton, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(utilityRow, utilityLayout);

        var actions = Ui.Row(this);
        actions.SetPadding(0, Ui.Dp(this, 10), 0, 0);
        // Главное действие — на всю ширину, ниже два оформленных действия рядом.
        _findButton = Ui.Pill(this, P, _finding ? "Проверяю серверы…" : "Проверить серверы",
            UiButtonKind.Primary, () => _ = FindNodesAsync(), Resource.Drawable.ic_nodes);
        CompactWideHeaderButton(_findButton);
        actions.AddView(_findButton, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        card.AddView(actions);

        // «Авто-проверка» — та же кнопка, которую приложение нажимает само:
        // фоновая проверка серверов и отсева. Раньше стояла отдельной строкой;
        // теперь делит ряд с «Поделиться», а «Сменить сервер» — на всю ширину ниже.
        _actionsRow2 = Ui.Row(this);
        _actionsRow2.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        _autoButton = Ui.Pill(this, P, Ui.IsCompactLayout ? "Авто: вкл" : "Авто-проверка: вкл",
            UiButtonKind.Secondary, ToggleAutoCheck, Resource.Drawable.ic_refresh);
        _shareButton = Ui.Pill(this, P, Ui.IsCompactLayout ? "Поделиться" : "Поделиться сервером",
            UiButtonKind.Secondary, ShareCurrentNode, Resource.Drawable.ic_export);
        CompactWrapHeaderButton(_autoButton);
        CompactWrapHeaderButton(_shareButton);
        _autoButton.TextSize = Ui.Sp(12f);
        // Оформление: главное действие — зелёный градиент, «Поделиться» — фиолетовая,
        // авто-проверка — бирюзовая; значки в тон текста.
        StyleHeaderAction(_findButton, P.Accent, filled: true);
        StyleHeaderAction(_autoButton, P.Universal, filled: false);
        StyleHeaderAction(_shareButton, P.Discord, filled: false);
        _actionsRow2.AddView(_autoButton, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _actionsRow2.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        _actionsRow2.AddView(_shareButton, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(_actionsRow2);

        // «Сменить сервер» теперь в строке состояния, рядом с «Отключить/Отмена».
        // «Добавить сервер» — в общем ряду служебных кнопок выше (вместе с MTProto).

        var probeStatusRow = Ui.Row(this);
        probeStatusRow.SetPadding(0, Ui.Dp(this, 8), 0, 0);
        _probeBadge = Ui.Badge(this, P, "○ АВТО-ПРОВЕРКА", P.MutedStrong);
        probeStatusRow.AddView(_probeBadge, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        _connectionStatus = Ui.Hint(this, "Загружаю серверы…", P);
        _connectionStatus.SetPadding(Ui.Dp(this, 8), 0, 0, 0);
        probeStatusRow.AddView(_connectionStatus, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(probeStatusRow);

        return card;
    }

    /// <summary>Кнопка с длинной подписью: две строки, перенос только по словам.</summary>
    private void CompactWrapHeaderButton(TextView button)
    {
        button.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 3), Ui.Dp(this, 8), Ui.Dp(this, 3));
        button.TextSize = Ui.Sp(11.5f);
        button.SetSingleLine(false);
        button.SetMaxLines(2);
        button.Gravity = Android.Views.GravityFlags.Center;
        button.SetMinHeight(Ui.Dp(this, 42));
        // Без переносов по слогам: «Подел/иться» выглядело как ошибка.
        button.BreakStrategy = Android.Text.BreakStrategy.Balanced;
        button.HyphenationFrequency = Android.Text.HyphenationFrequency.None;
    }

    /// <summary>Кнопка главного действия на всю ширину.</summary>
    private void CompactWideHeaderButton(TextView button)
    {
        button.TextSize = Ui.Sp(12.5f);
        button.SetSingleLine(true);
        button.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        button.SetMinHeight(Ui.Dp(this, 42));
    }

    /// <summary>Служебная кнопка шапки (MTProto, «Добавить»): одна строка, ниже основного действия.</summary>
    private void CompactHeaderUtilityButton(TextView button)
    {
        button.TextSize = Ui.Sp(12f);
        button.SetSingleLine(true);
        button.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        button.SetMinHeight(Ui.Dp(this, 38));
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
    /// Кнопка сервиса в шапке: нажатие подключает к сервису, а на самой кнопке —
    /// сколько серверов к нему готовы. Раньше это были отдельные кнопки на «Подключении»
    /// и некликабельные счётчики в шапке — теперь одно и то же.
    /// </summary>
    private LinearLayout ServiceHeaderButton(
        string label,
        int icon,
        NodeStore.QuickService service,
        Color tone,
        Color surface,
        out TextView? count,
        bool tintIcon = true)
    {
        var layout = Ui.Row(this);
        layout.SetPadding(Ui.Dp(this, Ui.IsCompactLayout ? 6 : 8), Ui.Dp(this, 6),
            Ui.Dp(this, Ui.IsCompactLayout ? 6 : 8), Ui.Dp(this, 6));
        layout.Background = Ui.Pressable(this, surface, Ui.Blend(surface, tone, 0.16f), 12,
            Color.Argb(120, tone.R, tone.G, tone.B));
        layout.Clickable = true;
        layout.Focusable = true;
        layout.Click += (_, _) => StartForService(service);
        layout.SetMinimumHeight(Ui.Dp(this, 40));

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

        var iconSide = Ui.Dp(this, Ui.IsCompactLayout ? 16 : 18);
        layout.AddView(image, new LinearLayout.LayoutParams(iconSide, iconSide));

        var text = Ui.Text(this, label, Ui.IsCompactLayout ? 11.5f : 12.5f, tone, bold: true);
        text.SetSingleLine(true);
        text.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        text.SetPadding(Ui.Dp(this, 6), 0, Ui.Dp(this, 4), 0);
        layout.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        // Счётчик сервиса — прямо на кнопке.
        count = Ui.Text(this, "—", Ui.IsCompactLayout ? 11.5f : 12.5f, tone, bold: true);
        count.SetSingleLine(true);
        layout.AddView(count, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        return layout;
    }

    /// <summary>
    /// Некликабельный счётчик (например, общее число рабочих серверов): иконка + число.
    /// Кнопки сервисов со счётчиками — <see cref="ServiceHeaderButton"/>.
    /// </summary>
    private LinearLayout CounterChip(int icon, Color tone, out TextView? count, bool tintIcon = true)
    {
        var chip = Ui.Row(this);
        chip.SetPadding(Ui.Dp(this, 6), Ui.Dp(this, 4), Ui.Dp(this, 6), Ui.Dp(this, 4));
        chip.Background = Ui.Rounded(Color.Argb(36, tone.R, tone.G, tone.B), 10, this,
            Color.Argb(120, tone.R, tone.G, tone.B));

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
        (int Total, int Working, int Universal, int Youtube, int Telegram, int Discord) summary,
        int workingServers)
    {
        if (_workingCount is not null)
        {
            // Рабочие — с иконкой-щитом и подписью «рабочих серверов».
            _workingCount.Text = workingServers.ToString();
        }

        if (_workingDelta is not null)
        {
            // Слот под дельту держим всегда (место зарезервировано), чтобы подпись
            // «рабочих серверов» не прыгала. При нуле показываем пустой слот.
            if (_workingChange == 0)
            {
                _workingDelta.Text = string.Empty;
                _workingDelta.Visibility = ViewStates.Invisible;
            }
            else
            {
                _workingDelta.Text = _workingChange > 0 ? $"+{_workingChange}" : $"−{-_workingChange}";
                _workingDelta.SetTextColor(_workingChange > 0 ? P.Positive : P.Warning);
                _workingDelta.Visibility = ViewStates.Visible;
            }
        }

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

    /// <summary>
    /// Значки сервисов у выбранного (подключённого) сервера — как блок сервисов
    /// в карточке отсева: пользователь сразу видит, что именно открывает текущий сервер.
    /// В отличие от счётчиков ниже, это не «сколько серверов готовы к сервису», а
    /// «какие сервисы открывает именно этот сервер».
    /// </summary>
    private void UpdateServerServices(ProxyNode? node, bool running)
    {
        if (_serverServicesRow is null || _serverServicesBadges is null)
        {
            return;
        }

        // Показываем только для реально подключённого сервера: до подключения значки
        // относились бы к серверу, через который трафик не идёт.
        if (!running || node is null)
        {
            // Пока нет подключения — заглушка «—»: блок не пустует и не прыгает по высоте.
            if (_serverServicesSignature != "none")
            {
                _serverServicesSignature = "none";
                _serverServicesBadges.RemoveAllViews();
                _serverServicesBadges.AddView(Ui.Badge(this, P, "—", P.Muted));
            }

            _serverServicesRow.Visibility = ViewStates.Visible;
            return;
        }

        // Пересобираем значки только когда сменился сервер или его отметки сервисов:
        // шапка обновляется каждые несколько секунд, лишние View на каждый такт не нужны.
        var signature = string.Join('|',
            node.Id,
            node.YoutubeOk == true,
            node.TelegramOk == true,
            node.DiscordOk == true,
            node.UniversalOk == true);
        if (signature == _serverServicesSignature)
        {
            _serverServicesRow.Visibility = ViewStates.Visible;
            return;
        }

        _serverServicesSignature = signature;
        _serverServicesBadges.RemoveAllViews();
        var any = false;

        if (node.YoutubeOk == true)
        {
            AddServiceBadge(Resource.Drawable.ic_service_youtube_brand, "YT", P.YouTube, tintIcon: false);
            any = true;
        }

        if (node.TelegramOk == true && !DeviceProfile.IsTelevision)
        {
            AddServiceBadge(Resource.Drawable.ic_service_telegram, "TG", P.Telegram);
            any = true;
        }

        if (node.DiscordOk == true && !DeviceProfile.IsTelevision)
        {
            AddServiceBadge(Resource.Drawable.ic_service_discord, "DS", P.Discord);
            any = true;
        }

        if (node.UniversalOk == true)
        {
            AddServiceBadge(
                DeviceProfile.IsTelevision ? Resource.Drawable.ic_service_zona : Resource.Drawable.ic_service_universal,
                DeviceProfile.IsTelevision ? "Zona" : "Другие", P.Universal);
            any = true;
        }

        if (!any)
        {
            // Сервер выбран, но отметок сервисов нет (старый кэш или ещё не проверен).
            var hint = Ui.Text(this, "Сервисы не отмечены", 11f, P.Muted);
            _serverServicesBadges.AddView(hint);
        }

        _serverServicesRow.Visibility = ViewStates.Visible;
    }

    /// <summary>Значок сервиса (иконка + подпись) для блока «Сервер открывает».</summary>
    private void AddServiceBadge(int icon, string label, Color tone, bool tintIcon = true)
    {
        if (_serverServicesBadges is null)
        {
            return;
        }

        var chip = Ui.Row(this);
        chip.SetPadding(Ui.Dp(this, 6), Ui.Dp(this, 3), Ui.Dp(this, 7), Ui.Dp(this, 3));
        chip.Background = Ui.Rounded(Color.Argb(40, tone.R, tone.G, tone.B), 9, this,
            Color.Argb(110, tone.R, tone.G, tone.B));

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

        chip.AddView(image, new LinearLayout.LayoutParams(Ui.Dp(this, 13), Ui.Dp(this, 13)));
        var text = Ui.Text(this, label, 11f, tone, bold: true);
        text.SetPadding(Ui.Dp(this, 4), 0, 0, 0);
        chip.AddView(text);

        var layout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        layout.RightMargin = Ui.Dp(this, 5);
        _serverServicesBadges.AddView(chip, layout);
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
                : "VPN отключён";
        if (_stateDot is not null)
        {
            _stateDot.SetTextColor(running ? P.Positive : connecting ? P.Warning : P.Muted);
        }

        if (_disconnect is not null)
        {
            // Значок питания виден всегда; без активности — приглушён и неактивен.
            var canDisconnect = running || serviceStarting || _connecting || CoreRuntime.IsRunning;
            _disconnect.Visibility = ViewStates.Visible;
            _disconnect.Enabled = canDisconnect;
            _disconnect.Alpha = canDisconnect ? 1f : 0.4f;
        }

        if (_nextNodeButton is not null)
        {
            // «Сменить сервер» виден всегда; без активности — приглушён и неактивен.
            var showNext = running && !_connecting;
            _nextNodeButton.Visibility = ViewStates.Visible;
            _nextNodeButton.Enabled = showNext;
            _nextNodeButton.Alpha = showNext ? 1f : 0.4f;
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
        if (_nodeText is not null && _nodeSubText is not null && _delayText is not null)
        {
            if (bypass)
            {
                _nodeText.Text = "TUN-обход";
                _delayText.Text = string.Empty;
                _nodeSubText.Text = "Прокси-сервер не используется";
            }
            else if (!running)
            {
                // Без активного туннеля не показываем остатки прошлой сессии.
                _nodeText.Text = connecting ? "Подключаюсь…" : "Сервер не подключён";
                _delayText.Text = string.Empty;
                _nodeSubText.Text = connecting ? "Выбираю проверенный сервер…" : "Нажмите кнопку сервиса";
            }
            else if (selected is null)
            {
                _nodeText.Text = "Сервер не выбран";
                _delayText.Text = string.Empty;
                _nodeSubText.Text = string.Empty;
            }
            else
            {
                _nodeText.Text = NodeDisplay.Name(selected);
                _delayText.Text = selected.DisplayDelay;
                _delayText.SetTextColor(rating >= 4 ? P.Positive : rating >= 2 ? P.Warning : P.Danger);

                // IP, скорость и звёзды — разными цветами.
                var sub = new Android.Text.SpannableStringBuilder();
                void Part(string text, Color color)
                {
                    if (sub.Length() > 0)
                    {
                        sub.Append(" · ");
                    }

                    var start = sub.Length();
                    sub.Append(text);
                    sub.SetSpan(new Android.Text.Style.ForegroundColorSpan(color), start, sub.Length(),
                        Android.Text.SpanTypes.ExclusiveExclusive);
                }

                if (!string.IsNullOrWhiteSpace(selected.Server))
                {
                    var endpoint = $"{selected.Server}:{selected.Port}";
                    // Если имя сервера — это сам адрес, второй раз не дублируем.
                    if (!_nodeText.Text.Contains(endpoint, StringComparison.OrdinalIgnoreCase))
                    {
                        Part(endpoint, P.MutedStrong);
                    }
                }

                if (selected.SpeedMbps is { } mbps)
                {
                    Part($"{mbps:0.#} Мбит/с", P.Accent);
                }

                Part(new string('★', rating) + new string('☆', 5 - rating),
                    rating >= 4 ? P.Positive : rating >= 2 ? P.Warning : P.Danger);
                _nodeSubText.SetText(sub, TextView.BufferType.Spannable);
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

        if (_trafficText is not null)
        {
            var show = NodeStore.Settings.ShowTraffic;
            _trafficText.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
            _trafficText.Text = !running || (_downBytes == 0 && _upBytes == 0)
                ? "Трафик: —"
                : $"Трафик: ↓ {FormatBytes(_downBytes)} · ↑ {FormatBytes(_upBytes)}";
        }

        // Что именно открывает подключённый сервер — значки сервисов под его данными.
        UpdateServerServices(selected, running);

        if (_connectionStatus is not null)
        {
            UpdateProbeBadge();
            UpdateAutoButton();
            if (!NodeStore.IsLoaded)
            {
                _connectionStatus.Text = _headerDetail is not null &&
                                         (_finding || DateTime.UtcNow < _headerDetailUntil)
                    ? _headerDetail
                    : "Загружаю серверы…";
                // Одна загрузка на всё время чтения кэша: раньше каждый такт шапки
                // запускал ещё одну задачу, и они толпились на одном замке.
                if (_loadingRequested)
                {
                    return;
                }

                _loadingRequested = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        NodeStore.EnsureLoaded();
                        RunOnUiThread(RefreshHeader);
                    }
                    catch (Exception exception)
                    {
                        _loadingRequested = false;
                        AppLog.Write("android", "Не удалось прочитать серверы", exception);
                        RunOnUiThread(() => _connectionStatus!.Text = "Серверы не прочитаны: " + exception.Message);
                    }
                });
                return;
            }

            try
            {
                var summary = NodeStore.Summary(requireFresh: false);
                // Значок «рабочих» — серверы из рабочего списка, которые реально что-то
                // открывают (объединение значков сервисов). Один сервер может открывать
                // несколько сервисов, поэтому сумма значков больше общего числа.
                var workingServers = summary.Working;
                // Последнее изменение числа рабочих фиксируем и держим, пока оно не сменится.
                if (_lastWorkingSeen < 0)
                {
                    _lastWorkingSeen = workingServers;
                }
                else if (workingServers != _lastWorkingSeen)
                {
                    _workingChange = workingServers - _lastWorkingSeen;
                    _lastWorkingSeen = workingServers;
                }

                UpdateServiceCounters(summary, workingServers);

                // Во время подключения показываем ход поиска сервера, а не сводку.
                // Фоновая проверка тоже видна: без этого казалось, что приложение молчит.
                var countText = $"В списке {summary.Total} серверов · рабочих {workingServers}";
                var probeActivity = ProbeClient.ActivityText;
                _connectionStatus.Text = _connecting && _connectText is not null
                    ? _connectText
                    : _headerDetail is not null && (_finding || DateTime.UtcNow < _headerDetailUntil)
                        ? _headerDetail
                        : probeActivity ?? countText;
                if (_findButton is not null && !_connecting)
                {
                    // «Остановить поиск» — только для ручного прогона: фоновая проверка
                    // не должна выглядеть так, будто пользователь её запустил.
                    _findButton.Text = _finding ? "Остановить поиск" : "Проверить серверы";
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
    /// и показывает ход прямо в шапке, не уводя на другие вкладки. Одно нажатие прогоняет
    /// несколько партий подряд (по 200 серверов), повторное нажатие останавливает поиск
    /// и текущую партию, и очередь следующих.
    /// </summary>
    private async Task FindNodesAsync()
    {
        if (_finding)
        {
            try
            {
                _findCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Прогон уже закончился.
            }

            ProbeClient.Cancel();
            // Кнопка обязана отвечать сразу: если прошлая партия ещё доигрывает отмену,
            // её завершение не должно съедать следующее нажатие.
            _finding = false;
            _headerDetail = "Поиск серверов остановлен";
            _headerDetailUntil = DateTime.UtcNow.AddSeconds(15);
            UpdateFindButton("Проверить серверы");
            RefreshHeader();
            return;
        }

        // Идёт фоновая партия: она уступает место ручному прогону, иначе нажатие
        // «Проверить серверы» просто отменяло бы её и ничего не запускало.
        if (ProbeClient.IsRunning)
        {
            ProbeClient.Cancel();
            await Task.Delay(800);
        }

        // Явное действие пользователя снимает прежнюю остановку.
        ProbeClient.Resume();
        _finding = true;
        UpdateProbeBadge();
        // Запоминаем, сколько рабочих было до прогона: в итоговой строке покажем
        // суммарное изменение за прогон.
        _workingBaseline = NodeStore.Summary(requireFresh: false).Working;
        var cts = new CancellationTokenSource();
        _findCts = cts;
        var first = $"Проверяю серверы: партия 1 из {ProbeClient.ManualBatches}…";
        _headerDetail = first;
        UpdateFindButton("Остановить поиск");
        RefreshHeader();

        try
        {
            await Task.Run(() => NodeStore.EnsureLoaded(), cts.Token);

            // Поиск новых серверов и перепроверка рабочих — разные проходы.
            // Рабочие не занимают места в партиях поиска: иначе одна и та же десятка
            // живых серверов проверялась бы в каждой партии.
            var (summary, batches) = await ProbeClient.RunBatchesAsync(
                ProbeClient.ManualBatches,
                NodeTester.MaxBatch,
                cts.Token,
                recheckWorking: false,
                batchStarted: (batch, total) => RunOnUiThread(() =>
                {
                    _headerDetail = $"Проверяю серверы: партия {batch} из {total}…";
                    RefreshHeader();
                }),
                progress: (batch, state) => RunOnUiThread(() =>
                {
                    // «Нашёл» — сколько НОВЫХ серверов открылось в этой партии,
                    // а не число повторно подтверждённых рабочих.
                    _headerDetail = state.Total > 0
                        ? $"Партия {batch} из {ProbeClient.ManualBatches}: " +
                          $"{state.Tested} из {state.Total} · нашёл {state.Working}"
                        : $"Проверяю серверы: партия {batch} из {ProbeClient.ManualBatches}…";
                    RefreshHeader();
                }),
                batchFinished: (batch, totalBatches, result) => RunOnUiThread(() =>
                {
                    // Итог партии: сколько проверено и сколько НОВЫХ в ней нашлось.
                    // Общее число и прибавка видны на значке под шапкой.
                    _headerDetail = result.Tested > 0
                        ? $"Партия {batch} из {totalBatches}: проверено {result.Tested}, " +
                          $"новых {result.NewWorking}"
                        : $"Партия {batch} из {totalBatches}: проверять нечего";
                    RefreshHeader();
                }));

            // Отдельный проход по уже рабочим серверам: их немного, и они важнее
            // найденных только что. Показываем его другой строкой.
            var recheckedWorking = 0;
            if (!cts.IsCancellationRequested && NodeStore.WorkingCount > 0)
            {
                RunOnUiThread(() =>
                {
                    _headerDetail = "Проверяю рабочие серверы…";
                    RefreshHeader();
                });
                var workingProgress = new Progress<ProbeProgress>(state => RunOnUiThread(() =>
                {
                    _headerDetail = state.Total > 0
                        ? $"Проверяю рабочие: {state.Tested} из {state.Total} · живых {state.Working}"
                        : "Проверяю рабочие серверы…";
                    RefreshHeader();
                }));
                var recheck = await ProbeClient.RunBatchAsync(
                    NodeTester.MaxBatch, cts.Token, workingProgress, workingOnly: true, manual: true);
                recheckedWorking = recheck.Tested;
            }

            var live = NodeStore.Summary(requireFresh: false).Working;
            var change = live - _workingBaseline;
            var changeText = change == 0
                ? string.Empty
                : change > 0 ? $" (+{change})" : $" (−{-change})";
            _headerDetail = cts.IsCancellationRequested
                ? "Поиск серверов остановлен"
                : summary.Tested == 0
                    ? summary.Error ?? "Проверять нечего: все серверы проверены недавно"
                    : $"Проверено {summary.Tested} серверов за {batches} " +
                      $"{(batches == 1 ? "партию" : batches < 5 ? "партии" : "партий")}" +
                      (recheckedWorking > 0 ? $", рабочих {recheckedWorking}" : string.Empty) +
                      $": рабочих {live}{changeText}";
        }
        catch (System.OperationCanceledException)
        {
            _headerDetail = "Поиск серверов остановлен";
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Проверка серверов из шапки не удалась", exception);
            _headerDetail = "Проверка серверов не удалась: " + exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_findCts, cts))
            {
                _findCts = null;
            }

            cts.Dispose();
            _finding = false;
            _headerDetailUntil = DateTime.UtcNow.AddSeconds(20);
            UpdateProbeBadge();
            // Итог прогона — в кэш сразу, не дожидаясь пятиминутной очереди.
            _ = Task.Run(() => NodeStore.SaveNodes(force: true));
            UpdateFindButton("Проверить серверы");
            RefreshHeader();
        }
    }

    /// <summary>
    /// Кнопка авто-проверки: показывает, включена ли она, и переключает её.
    /// Включена — приложение само проверяет серверы и отсев; выключена — только вручную.
    /// </summary>
    private void UpdateAutoButton()
    {
        if (_autoButton is null)
        {
            return;
        }

        var enabled = SettingsStore.Current.IdleTestEnabled &&
                      AndroidSettingsStore.Current.AutoProbe;
        var autoLabel = Ui.IsCompactLayout ? "Авто: " : "Авто-проверка: ";
        _autoButton.Text = (enabled ? "● " : "○ ") + autoLabel + (enabled ? "вкл" : "выкл");
        _autoButton.SetTextColor(enabled ? P.Positive : P.MutedStrong);
    }

    private void ToggleAutoCheck()
    {
        var settings = SettingsStore.Current;
        var enabled = !settings.IdleTestEnabled;
        settings.IdleTestEnabled = enabled;
        AndroidSettingsStore.Current.AutoProbe = enabled;
        SettingsStore.Save();
        AndroidSettingsStore.Save();
        if (enabled)
        {
            ProbeScheduler.Start(this);
        }
        else
        {
            ProbeScheduler.Stop();
        }

        UpdateAutoButton();
        Ui.Toast(this, enabled ? "Авто-проверка включена" : "Авто-проверка выключена");
        RefreshHeader();
    }

    private void UpdateFindButton(string text) => RunOnUiThread(() =>
    {        if (_findButton is not null)
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
    private CancellationTokenSource? _findCts;
    private bool _loadingRequested;
    private bool _switchingNode;
    private string? _headerDetail;
    private DateTime _headerDetailUntil = DateTime.MinValue;
    private TextView? _findButton;
    private TextView? _autoButton;

    private LinearLayout? _nextNodeButton;

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

        if (RejectedCheckService.IsRunning || NodeTester.IsRunning ||
            RejectedBootCheckService.IsRunning)
        {
            Ui.Toast(this, "Дождитесь окончания перепроверки отсева");
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

        if (!VpnStarter.CanStartServer(out var blockedReason))
        {
            Ui.Toast(this, blockedReason);
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
        Ui.StyleSystemText(input, 14.5f);
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

                await Task.Run(() => NodeStore.AddManual(node));
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
            if (!VpnStarter.CanStartServer(out var blockedReason))
            {
                Ui.Toast(this, blockedReason);
                _pendingConfig = null;
                return;
            }

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
        // На телевизоре вторая кнопка называется «Zona» — это тот же универсальный доступ.
        _ => DeviceProfile.IsTelevision ? "Zona" : "универсальный",
    };

    /// <summary>
    /// Бейдж режима проверки: ручная / автоматическая / завершена / выключена.
    /// Отдельная цветная плашка, чтобы в шапке было видно, кто именно проверяет серверы.
    /// </summary>
    private void UpdateProbeBadge()
    {
        if (_probeBadge is null)
        {
            return;
        }

        var manual = _finding || ProbeClient.IsManualRun;
        var running = ProbeClient.IsRunning;
        var recent = ProbeClient.ActivityText is not null;
        var completed = !manual && !running && (recent ||
            (_headerDetail is not null && DateTime.UtcNow < _headerDetailUntil));
        var off = NodeStore.Settings.AutomationPaused ||
                  !NodeStore.Settings.IdleTestEnabled ||
                  ProbeClient.IsPaused;

        string text;
        Color tone;
        if (manual)
        {
            text = "● РУЧНАЯ ПРОВЕРКА";
            tone = P.Warning;
        }
        else if (running)
        {
            text = "● АВТО-ПРОВЕРКА";
            tone = P.Telegram;
        }
        else if (completed)
        {
            text = "✓ ПРОВЕРКА ЗАВЕРШЕНА";
            tone = P.Positive;
        }
        else if (off)
        {
            text = "○ АВТО-ПРОВЕРКА ВЫКЛ";
            tone = P.Muted;
        }
        else
        {
            text = "○ АВТО-ПРОВЕРКА";
            tone = P.MutedStrong;
        }

        // Шапка обновляется каждые 3 секунды и ещё чаще по ходу проверки. Новый
        // GradientDrawable и строка на каждый такт — лишние Java-объекты и лишние
        // сборки мусора; при редком дедлоке GC это лишний шанс зависнуть.
        if (!string.Equals(_probeBadgeText, text, StringComparison.Ordinal))
        {
            _probeBadgeText = text;
            _probeBadge.Text = text;
        }

        if (_probeBadgeTone != tone)
        {
            _probeBadgeTone = tone;
            _probeBadge.SetTextColor(tone);
            _probeBadge.Background = Ui.Rounded(
                Color.Argb(42, tone.R, tone.G, tone.B), 8, this,
                Color.Argb(115, tone.R, tone.G, tone.B));
        }

        var statusTone = manual || running ? P.TextStrong : P.Muted;
        if (_connectionStatusTone != statusTone)
        {
            _connectionStatusTone = statusTone;
            _connectionStatus?.SetTextColor(statusTone);
        }
    }

    private void SetConnectionStatus(string text)
    {
        RunOnUiThread(() =>
        {
            if (_connectionStatus is not null)
            {
                _connectionStatus.Text = text;
            }

            UpdateProbeBadge();
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
        _ = Task.Run(async () =>
        {
            try
            {
                Maintenance.EnsureScheduled(this);
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
